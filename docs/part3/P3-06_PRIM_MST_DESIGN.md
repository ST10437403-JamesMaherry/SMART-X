# P3-06 - Prim minimum spanning tree and automatic mesh rerouting

**GitHub issue:** `[P3-06] Implement Prim MST and automatic mesh rerouting`
**Branch:** `feature/part3-prim-mst`
**Dependency:** P3-05 BFS routing merged into `dev` before branching.

This note describes the implemented Commit 1 Prim foundation and Commit 2
weighted tree-route and connectivity projections, plus the boundaries of
remaining work. It is a design and verification plan, not a claim that local
builds, test runs, benchmark measurements, or CI have passed.

## Commit 1: Gateway-rooted weighted Prim backbone

### Design

- `MeshSpanningTreeService.GetGatewayMinimumSpanningTreeAsync()` obtains exactly
  one immutable, registry-reconciled `MeshGraphSnapshot` from the existing
  `IMeshTopologyStore`, then computes a new result. It neither mutates the
  store nor invents sensor registrations or radio connections.
- `CalculateGatewayMinimumSpanningTree(snapshot)` is the pure, testable algorithm.
  It builds adjacency from **configured undirected links** whose **two endpoints**
  are forwarding-eligible, starts at `MeshNodeKey.Gateway`, and expands its
  reached component using `.NET PriorityQueue<FrontierEdge, FrontierPriority>`.
- Every candidate edge crosses from a visited parent to an unvisited child at
  enqueue time. When extracted, the child is accepted only if
  `HashSet<MeshNodeKey>.Add` succeeds, so cycles and competing stale candidates
  cannot create a second parent or duplicate selected link.
- Priority order is deterministic: `(SimulatedEnergyCost, NodeA, NodeB, LinkId)`.
  The endpoints are **canonical undirected identities**, not traversal-order
  parent/child identities. Equal-cost choices are therefore reproducible even
  when original link input order differs.
- Prim minimises the **sum of selected link weights** of the gateway-connected
  component, not the hop count, and not necessarily the best point-to-point
  weighted route to any sensor. BFS in P3-05 remains the *fewest-hop* algorithm.
- The gateway has no parent. One `MeshTreeBranch` records each selected sensor's
  parent, child, and exact original `MeshLink`. The source link remains
  undirected; the parent-child orientation belongs only to the selected tree.

### Forwarding and disconnected-graph policy

| Sensor status | Eligible for MST forwarding? |
| --- | --- |
| Online | Yes |
| Warning | Yes |
| Offline | No |
| Fault | No |
| Maintenance | No |
| Decommissioned | No |

The gateway is always eligible. An Offline or otherwise unavailable sensor may
remain in the authoritative snapshot, but does not enter Prim's temporary
adjacency; its configured links are **not** silently deleted. A healthy sensor
isolated from the gateway is absent from this gateway-rooted tree. The selected
backbone is intentionally **not** a spanning forest across disconnected islands.
Commit 2 explicitly classifies disconnected and unavailable sensors and
reconstructs sensor-to-gateway **tree paths**. It does not claim those paths
are globally least-cost point-to-point paths.

### Result contract

- `MinimumSpanningTreeResult.GraphVersion`: source snapshot revision.
- `SelectedBranches`: immutable, child-sorted parent/child/link associations.
- `SelectedLinks`: immutable, canonical undirected link list without duplicates.
- `ReachedNodes`: gateway plus connected sensors in key order.
- `TotalSimulatedEnergyCost`: finite sum of selected link weights in **relative
  simulated weight units**. These are **not** measured watts, joules, radio
  signal levels, or validated real-world energy savings.
- `SelectedEdgeCount == ReachedNodeCount - 1`: gateway-tree cardinality.
- `TryGetParent`, `TryGetBranch`, `ContainsNode`: O(1) expected identity lookups.
  `ContainsNode(false)` alone does not distinguish an unregistered sensor from
  a registered but unavailable/disconnected sensor; the separate Commit 2
  routing projection supplies those classifications.

The domain constructor independently rejects invalid versions, malformed
branches, duplicate children, repeated link identities/pairs, non-gateway
roots, parent cycles, and aggregate weights that overflow to nonfinite values.
Inputs are defensively copied and exposed through read-only wrappers.

### Hand-verified example

| Configured undirected edge | Simulated cost | Chosen MST edge? |
| --- | ---: | --- |
| GW-A | 1 | Yes |
| GW-B | 4 | No |
| A-B | 2 | Yes |
| A-C | 10 | No |
| B-C | 3 | Yes |
| GW-C | 20 | No |

MST selection is `GW-A (1), A-B (2), B-C (3)` with total cost **6**;
three selected edges span the four reachable vertices. By contrast, a direct
`GW-A (100)` link is one hop, even if a cheaper `GW-B (1) + B-A (1)` two-link
route exists: BFS and Prim intentionally answer different questions.

### Complexity

For V snapshot vertices, E configured undirected links, and R vertices reached
from the gateway:

- Build forwarding-eligible adjacency: **O(V + E)** time and memory.
- Each eligible undirected edge enters the edge-frontier heap at most once.
  Edge-heap Prim: **O(E log E)** time in the general case, **O(V + E)** auxiliary
  memory including adjacency, heap, and selected branches.
- Immutable result validation and canonical output sorting:
  **O(R log R)** time, with O(R) additional memory. This is included in the
  end-to-end pure calculation, not hidden from the analysis.
- The P3-04 store's registry reconciliation and sorted snapshot creation occur
  *before* the pure algorithm and have their own documented cost.

## Test coverage for Commit 1

- `tests/SmartX.Domain.Tests/Topology/MinimumSpanningTreeResultTests.cs`:
  parent/child/link validation, gateway-only tree, exact parent relationships,
  order, defensive copies, invalid roots/cycles, reused links, duplicate
  parents, invalid keys, and nonfinite aggregate rejection.
- `tests/SmartX.Application.Tests/Topology/MeshSpanningTreeServiceTests.cs`:
  exact weighted graph/selected edges/cost, deterministic ties, cycle
  avoidance, disconnected islands, unavailable bridges, Warning relay,
  independent BFS-vs-Prim objective, cancellation, and single-snapshot reads.

Run from the repository root:

```bash
# P3-06 Commit 1 - focused new tests

dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MinimumSpanningTreeResultTests"
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeServiceTests"

# Full regression/quality gates

dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

Record **actual** local/CI totals and any failures after running these checks;
archive validation alone is not a C# compilation or xUnit run.

## Commit 2: Tree routes, accumulated costs and complete connectivity summary

### One-snapshot projection and separation of algorithms

- `MeshSpanningTreeService.GetGatewaySpanningTreeRoutingAsync()` calls
  `IMeshTopologyStore.GetSnapshotAsync()` **once** per request, then passes the
  same immutable `MeshGraphSnapshot` to the pure
  `CalculateGatewaySpanningTreeRouting(snapshot)` method.
- **Prim** calculates the chosen minimum-total-cost gateway backbone. Existing
  P3-05 **BFS** runs on that **same** snapshot **only** to classify registered
  sensors and their disconnected eligible components. It does **not** supply
  the tree's parent links or its selected route edges.
- A consistency check requires Prim and BFS to agree on the exact
  forwarding-eligible gateway-connected vertex set and source graph revision.
  This prevents an accidental eligibility-policy mismatch from being exposed.
- `MeshGatewaySpanningTreeRouting` publishes the original immutable `Tree`,
  its `TotalSimulatedEnergyCost`, `SelectedEdgeCount`, and the counts and
  canonical identities for `RegisteredSensors`, `UnavailableSensors`, and
  `DisconnectedComponents`.
- Every registered sensor belongs to **exactly one** category: reached by
  Prim, unavailable due to sensor status, or in one gateway-disconnected
  eligible component. The constructor rejects missing, repeated and
  overlapping memberships, unsorted sensor identities and malformed groups.
- An **unknown sensor ID** is not silently labelled offline: the typed
  `TryGetTreeRouteToGateway` method returns `false` with `null`. Registered
  unavailable/disconnected devices return explicit `MeshRouteStatus` values.

### Selected tree path versus total backbone cost

`MeshGatewaySpanningTreeRouting.TryGetTreeRouteToGateway(sensor, out route)`
reconstructs the path by following `MinimumSpanningTreeResult.TryGetBranch`
from the selected sensor through its unique parents until the gateway.
The immutable `MeshTreeRoute` includes:

- `GraphVersion`, `Sensor`, and `Status` (`Reachable`, `Disconnected`, or
  `Unavailable`) matching the one source graph revision.
- `PathToGateway`: sensor -> ... -> gateway for a reached sensor; otherwise
  empty. `SelectedLinksToGateway` contains those **actual selected MST links**
  in the same order, not merely all configured edges.
- `HopCount`: the number of selected links, or **null** when unreachable.
- `AccumulatedSimulatedEnergyCost`: the sum of that sensor's tree-path weights,
  or **null** when unreachable. A null cost is intentional: a missing route
  must never be displayed as a real **zero-cost** path.

`TotalSimulatedEnergyCost` belongs to the entire chosen **tree backbone**. It
is **not** the same metric as one sensor's `AccumulatedSimulatedEnergyCost`.
Routes are reconstructed on demand in **O(h)** time and memory for h hops,
not precalculated or cached for every sensor. Every route and selected link
is defensively copied and validated before publication.

### Numeric example: the MST need not have the shortest weighted route

| Configured undirected edge | Relative simulated cost | Selected by Prim? |
| --- | ---: | --- |
| GW-A | 1 | Yes |
| A-B | 1 | Yes |
| B-C | 1 | Yes |
| GW-C | 2.5 | No |

The gateway-rooted MST has total backbone cost **3** and routes C through
`C -> B -> A -> GW`, with **3 hops** and **tree-path cost 3**. The separate
configured `C -> GW` edge has weight **2.5**, which is cheaper for this
individual device but would increase the tree's **overall** total cost if
chosen in place of `B-C`. **Prim is not Dijkstra** and this implementation
makes no shortest-weighted-sensor-route guarantee.

### Forwarding state and disconnected groups

A sensor in `Online` or `Warning` may forward. `Offline`, `Fault`,
`Maintenance`, and `Decommissioned` sensors cannot forward. A healthy
sensor behind an unavailable bridge is **Disconnected** (not Unavailable).
Configured edges touching an unavailable sensor remain in the source graph
unless the P3-04 store has removed them due to decommissioning. Isolated
eligible sensors and cyclic islands are grouped into canonical disconnected
components, not added to the gateway-rooted tree.

### Complexity and isolation

- Prim: **O(E log E)** heap work plus **O(V + E)** adjacency construction;
  immutable tree result ordering adds **O(R log R)** for R reached nodes.
- BFS connectivity classification on the same snapshot: **O(V + E)**.
- One target's on-demand tree-route construction: **O(h)** time and memory.
- Snapshot creation and registry reconciliation are P3-04 store costs, not
  concealed as part of the pure algorithm.
- Each public asynchronous projection reads the store once; the service
  does not retain a stale tree or a global route cache. Commit 3 will test
  live mutation/failure/recovery with actual registry and link operations.

### Test coverage for Commit 2

- `tests/SmartX.Domain.Tests/Topology/MeshSpanningTreeRoutingContractTests.cs`:
  immutable path/link copies, version and sensor validation, adjacency and
  cycle rejection, unavailable/disconnected null-cost routes, malformed
  classifications, component ordering, unknown IDs and overflow.
- `tests/SmartX.Application.Tests/Topology/MeshSpanningTreeRoutingServiceTests.cs`:
  exact selected path/weight fixtures, BFS-vs-Prim path distinction, MST vs
  shortest weighted path distinction, islands, unavailable bridges, Warning
  relays, deterministic equal-cost ties, no stale snapshot caching,
  cancellation and typed per-device lookup.

```bash
# P3-06 Commit 2 focused checks

dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRoutingContractTests"
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRoutingServiceTests"

# Full regression and quality gate

dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

Record the **actual** local results and GitHub Actions evidence after running
these commands. Archive/static verification is not a claim of compiled tests.

## Remaining planned commits

1. **Completed:** `feat(topology): calculate Prim MST using a priority heap`.
2. **Completed in this change:** `feat(topology): expose MST-backed routes and cost summaries`.
3. **Next:** `feat(topology): recompute routes after mesh and sensor failures` -
   demonstrate correct revision changes and real alternate-link failover.
4. `test(topology): prove MST optimality and self-healing scenarios` -
   independent minimality checks, fixed weighted examples, bridge failures,
   disconnected topologies, and state reactivation.

P3-07 will expose typed topology HTTP endpoints and integrate read-only
projections with the UI. P3-06 does not add public API endpoints or pretend
that physical topology or RF quality can be inferred from graph coordinates.
