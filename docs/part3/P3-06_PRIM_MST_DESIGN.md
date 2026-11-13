# P3-06 - Prim minimum spanning tree and automatic mesh rerouting

**GitHub issue:** `[P3-06] Implement Prim MST and automatic mesh rerouting`
**Branch:** `feature/part3-prim-mst`
**Dependency:** P3-05 BFS routing merged into `dev` before branching.

This note describes the implemented Commit 1 Prim foundation, Commit 2
weighted tree routes, and Commit 3 version-aware recalculation after explicit
mesh and sensor changes. It also records the remaining proof work. It is a design and verification plan, not a claim that local
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
  does not retain a stale tree or a global route cache. Commit 3 adds an
  explicit version-aware before/after refresh without a second mutable tree
  store or extra radio edges.

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

## Commit 3: Recompute after explicit mesh/sensor changes

### Version-aware refresh contract

`MeshSpanningTreeService.RefreshGatewaySpanningTreeRoutingAsync(previous, token)`
reads **one** authoritative, registry-reconciled `MeshGraphSnapshot`.
`CalculateGatewaySpanningTreeRefresh(previous, snapshot, token)` exposes the
identical, pure calculation for deterministic tests.

1. **Same graph version:** Reuse the supplied immutable `previous` projection
   without recalculating Prim or BFS. No route transition is fabricated.
2. **Newer graph version:** Recalculate Prim's selected tree and BFS's
   connectivity classifications from the **same new snapshot**, then create
   `MeshSpanningTreeRefresh(Previous, Current)`.
3. **Older graph version:** Reject a backwards revision instead of claiming
   an older topology is current. A previous result must come from the same
   process-local store instance. Process restart requires a fresh initial
   read, not comparison of version counters across processes.
4. **Cancellation:** Propagates cancellation; no partial refresh is published.

The service performs **no writes** to the graph, sensor registry or existing
links. Configured alternative links must already exist. A newer mesh operation
that completes after the snapshot is read belongs to the **next** refresh,
not this already versioned result.

### Result classification and meaningful differences

- `MeshSpanningTreeRefresh`: immutable `Previous`, `Current`, revisions,
  `HasGraphRevisionChanged`, `HasBackboneChanged`,
  `HasDisconnectedComponentsChanged`, and canonical `RouteTransitions`.
- `MeshTreeRouteTransition`: sensor ID, optional old/new tree routes, and
  independent flags for registration/removal, gateway connectivity loss or
  recovery, selected-route changes and accumulated simulated cost changes.
- Old/new routes are compared **semantically**, not merely by graph revision.
  Equal parent paths with new selected costs are reported as a cost change,
  **not** falsely claimed to be a reroute.
- A revision caused by an unused link-cost update or Online -> Warning status
  change does not falsely change selected tree paths. Nonetheless the new
  revision and current sensor status remain accessible in graph snapshots.
- Disconnected islands may merge/split without changing any individual
  sensor's `Disconnected` status; the component-change flag handles this.
- A missing registered sensor is represented by a null old/new route. This
  is not conflated with an existing Offline or Fault sensor.
- Immutable previous results remain valid audit evidence, even when the new
  projection excludes previously selected links or unavailable relays.

### Deterministic before/after example

The simulated mesh contains gateway GW, preferred relay A, backup relay B
and leaf L. **All four edges are configured before the failure.**

| Undirected link | Relative simulated cost |
| --- | ---: |
| GW -- A | 1 |
| A -- L | 1 |
| GW -- B | 2 |
| B -- L | 3 |

| Snapshot | Prim selected edges | Total tree cost | L's tree route | L's path cost |
| --- | --- | ---: | --- | ---: |
| Before (all Online) | GW--A, A--L, GW--B | 4 | L -> A -> GW | 2 |
| A Offline | GW--B, B--L | 5 | L -> B -> GW | 5 |
| A Online again | GW--A, A--L, GW--B | 4 | L -> A -> GW | 2 |

A becomes `Unavailable` while Offline; L remains `Reachable` because an
**already configured** fallback path exists. All four links remain in the
source snapshot for Offline status. With **only** GW--A and A--L configured,
A's failure instead produces `Unavailable(A)` and `Disconnected(L)` with an
empty gateway MST; no self-healing can be claimed. Decommissioning is different
from Offline: P3-04 intentionally removes its incident simulated links.

### Cost/complexity and scope

The basic Prim computation remains **O(E log E)**; BFS classification is
**O(V + E)**. Same-revision refresh costs a single store snapshot read, then
reuses the immutable projection. A newer revision recomputes both algorithms.
Comparison of chosen branches/registered/unavailable sensors is **O(V)**.
When selected routes change, reconstructing all affected paths can take
**O(sum of route lengths)**, worst-case O(V²) on a long chain; this extra
comparison is optional orchestration, not part of Prim's heap complexity.
The result retains only changed routes, not a full copied route per sensor.

No new HTTP endpoints, background watchers, SignalR subscribers, automatic
radio configuration, or concurrency-breaking shadow topology are added.
P3-07 will expose these read projections and explicit simulation mutations
through typed APIs. `RefreshGatewaySpanningTreeRoutingAsync` performs a
fresh read when called after a change; it does **not** run itself in a
background process.

### Commit 3 verification

- `tests/SmartX.Domain.Tests/Topology/MeshSpanningTreeRefreshTests.cs`
  validates immutable transition semantics, registration/removal, revision
  ordering, connectivity changes, cost-only changes and component changes.
- `tests/SmartX.Application.Tests/Topology/MeshSpanningTreeRefreshServiceTests.cs`
  tests known before/after graph revisions without mutable infrastructure,
  including numeric alternate-tree costs and failure without redundancy.
- `tests/SmartX.Infrastructure.Tests/Topology/MeshPrimReroutingLiveTopologyTests.cs`
  uses the **real** sensor registry and synchronized mesh store to verify
  status changes, decommissioning, link removal, cost updates, isolated
  registration and recovery through existing links.
- `docs/part3/P3-06_REROUTING_SCENARIOS.md` documents the exact fixtures,
  expected outcomes and commands without fabricating test run counts.

```bash
# New Commit 3 suites

dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRefreshTests"
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRefreshServiceTests"
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MeshPrimReroutingLiveTopologyTests"

# Full quality gate

dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

## Commit 4 - independent optimality and adversarial rerouting evidence

- `tests/SmartX.Application.Tests/Topology/MeshPrimOptimalityAdversarialTests.cs`
  exhaustively evaluates **1,216** tiny undirected graph/weight
  configurations against an independently implemented Kruskal union-find
  minimum-spanning-forest oracle. Only Kruskal's **gateway component** is
  compared with the gateway-rooted Prim result.
- Each result must have exactly reached-vertices-minus-one selected links,
  real configured link IDs and weights, and no cycles. The independent
  cycle/exchange certificate also rules out an omitted cheaper link that
  could replace a higher-cost edge on its selected tree path.
- `tests/SmartX.Infrastructure.Tests/Topology/MeshPrimSelfHealingAdversarialTests.cs`
  checks three successive relay failures, all unavailable-relay statuses,
  no-backup disconnection, disconnected-island splitting and reconnection,
  unused-cost changes, and parent-path changes against the real registry
  and synchronized mesh store.
- `docs/part3/P3-06_MST_OPTIMALITY_VERIFICATION.md` provides all deterministic
  expected selected edges, totals, routes, commands and evidence boundaries.
  These are **test expectations**, not a statement that the tests ran.

## Planned commit completion

1. `feat(topology): calculate Prim MST using a priority heap` - implemented.
2. `feat(topology): expose MST-backed routes and cost summaries` - implemented.
3. `feat(topology): recompute routes after mesh and sensor failures` - implemented.
4. `test(topology): prove MST optimality and self-healing scenarios` - tests and
   documentation prepared; verify with actual .NET and CI output before merge.

P3-07 will expose typed topology HTTP endpoints and integrate read-only
projections with the UI. The graph cost remains a **simulated relative
weight**, not measured energy consumption or verified RF connectivity.
