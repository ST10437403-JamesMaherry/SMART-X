# P3-06 - Prim minimum spanning tree and automatic mesh rerouting

**GitHub issue:** `[P3-06] Implement Prim MST and automatic mesh rerouting`  
**Branch:** `feature/part3-prim-mst`  
**Dependency:** P3-05 BFS routing merged into `dev` before branching.

This note describes the implemented Commit 1 foundation and the boundaries of
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
Commit 2 will explicitly classify disconnected and unavailable sensors and
reconstruct sensor-to-gateway **tree paths**, not claim they are globally
least-cost point-to-point paths.

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
  a registered but unavailable/disconnected sensor; classification comes later.

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

## Remaining planned commits

1. **Completed in this change:** `feat(topology): calculate Prim MST using a priority heap`
2. **Next:** `feat(topology): expose MST-backed routes and cost summaries` -
   classify every sensor; provide gateway tree paths and their summed weights.
3. `feat(topology): recompute routes after mesh and sensor failures` -
   demonstrate correct revision changes and real alternate-link failover.
4. `test(topology): prove MST optimality and self-healing scenarios` -
   independent minimality checks, fixed weighted examples, bridge failures,
   disconnected topologies, and state reactivation.

P3-07 will expose typed topology HTTP endpoints and integrate read-only
projections with the UI. P3-06 does not add public API endpoints or pretend
that physical topology or RF quality can be inferred from graph coordinates.
