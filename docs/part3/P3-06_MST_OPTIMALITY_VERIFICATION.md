# P3-06 Commit 4 - MST optimality and self-healing verification

**Issue:** `[P3-06] Implement Prim MST and automatic mesh rerouting`  
**Branch:** `feature/part3-prim-mst`  
**Commit:** `test(topology): prove MST optimality and self-healing scenarios`

This document specifies **expected** outcomes, independent mathematical checks,
and repeatable commands. It is not a claim that a local or CI run has passed.
Record the actual build/test/format output in the pull request after testing.

## 1. Mathematical proof strategy

`MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree` uses a real
`PriorityQueue` frontier and a visited set, rooted at the single gateway.
`MeshPrimOptimalityAdversarialTests` independently verifies the result with a
**Kruskal disjoint-set (union-find) oracle**, which does not reuse Prim's heap,
parent selection, or route reconstruction.

1. Extract **forwarding-eligible** nodes and configured undirected links from
   one immutable `MeshGraphSnapshot`. Online and Warning may forward; Offline,
   Fault, Maintenance, and Decommissioned may not. A decommissioned sensor is
   also forbidden to retain incident links in a valid source snapshot.
2. Use an independent union-find connectivity pass to determine the component
   containing the gateway, including the isolated-gateway case.
3. Use Kruskal over eligible edges sorted by cost and stable identities to
   obtain a **minimum spanning forest**. Compare only the component containing
   the gateway with the gateway-rooted Prim result; other islands are not
   silently included in the backbone total.
4. Assert equal reached vertices and **selected edge count = reached vertices
   minus one**, equal total cost, real configured link IDs/costs, and zero
   cycles. Every tree branch must reference an existing eligible edge.
5. Independently check the **MST cycle/exchange certificate**: for each
   unselected eligible edge joining gateway-reached vertices, its weight is
   greater than or equal to the maximum weight on the selected tree path
   connecting its endpoints. A cheaper excluded edge would contradict the
   claimed minimum-cost tree.

### Exhaustive small-graph coverage

| Configuration | Undirected pairs | Edge subsets | Weight profiles | Graphs checked |
| --- | ---: | ---: | --- | ---: |
| Gateway + 3 sensors | 6 | `2^6 = 64` | Unique integer, all equal, repeated fractional | **192** |
| Gateway + 4 sensors | 10 | `2^10 = 1,024` | Repeated fractional | **1,024** |
| **Total** | | | | **1,216** |

The masks include connected networks, cycles, disconnected components,
isolated vertices, all edges absent, and complete graphs. All test graph links
have strictly positive finite simulated weights, so exact binary-fraction
comparisons are appropriate. The independent Kruskal oracle and cycle
certificate intentionally form an additional guard against a faulty fixture's
hard-coded expected total.

Equal-cost dense-graph fixture: gateway + four sensors, all ten undirected
links cost `1`, expected **four selected gateway-star edges** and total tree
cost **4**. The deterministic identity tie-break is exercised without relying
on hash-map insertion order.

Distinct optimization objective fixture: `GW--A (1)`, `A--B (1)` and
`GW--B (1.5)` produce a **minimum-total-cost tree of 2** containing GW--A
and A--B. The selected **B-to-gateway tree path costs 2**, whereas the
**excluded direct link costs only 1.5** and BFS takes just one hop. This
explicitly demonstrates why a Prim MST does not guarantee the minimum
weighted route for every individual sensor.

## 2. Sensor status and route correctness

- Online and Warning nodes participate in tree construction.
- Offline, Fault and Maintenance relays are excluded from the selected tree,
  but their configured links remain in the source snapshot.
- An unavailable relay has an explicit `Unavailable` route. Its previously
  reachable downstream sensor becomes either `Reachable` via an existing
  backup or `Disconnected` with empty path and null accumulated cost.
- An eligible sensor in a disconnected island cannot be labeled as
  gateway-reachable. The sum of weights along each published tree route equals
  the sum of its actual selected link objects.
- Minimum **total backbone cost** is distinct from minimum **individual path
  cost** or fewest hops. The tests do not describe Prim as Dijkstra.

## 3. Live-store failure and recovery fixtures

All fixtures mutate **real** `InMemorySensorRepository` and
`InMemoryMeshTopologyStore` objects via their public interfaces. Prim and its
refresh method only read immutable graph snapshots; no radio links are
created automatically.

### A. Three configured relays with successive failures

Initial explicit links (six): `GW--A (1)`, `A--L (1)`, `GW--B (2)`,
`B--L (2)`, `GW--C (3)`, and `C--L (4)`.

| Registry state | Selected tree | Total | Leaf's selected tree route | Route cost |
| --- | --- | ---: | --- | ---: |
| All Online | GW--A, A--L, GW--B, GW--C | **7** | L -> A -> GW | **2** |
| A Offline | GW--B, B--L, GW--C | **7** | L -> B -> GW | **4** |
| A Offline, B Fault | GW--C, C--L | **7** | L -> C -> GW | **7** |
| A Offline, B Fault, C Maintenance | none | **0** | L Disconnected | none |
| Recover B Online | GW--B, B--L | **4** | L -> B -> GW | **4** |

The source graph retains **six** links throughout. Connectivity fails
explicitly after all relays become unavailable; it is not falsely called
self-healing. Historical results remain unchanged after each refresh.

### B. Nonredundant bridge (three health states)

Only `GW--A (2)` and `A--L (3)` exist. For each of Offline, Fault and
Maintenance on A:

- Before: total tree cost **5**, leaf route `L -> A -> GW`.
- Failure: total **0**, A is `Unavailable`, L is `Disconnected`, and L has no
  route or cost. Both configured links remain in the snapshot.
- Recovery to Online: original route and total **5** return; no new links.

### C. Split an island, then explicitly restore its bridge

Initial links: `GW--A (1)`, `A--B (1)`, `B--C (3)`, `C--D (1)`.

- Initially: four selected edges, tree cost **6**, D reaches GW via C, B, A.
- Remove `B--C`: cost **2**, C and D become one disconnected component; no
  invented alternative edge is allowed.
- Explicitly add **new** `B--C (5)`: cost **8**, C and D regain connectivity.
  The previous six-cost snapshot and routes remain immutable.

### D. Unused edge cost versus genuine reroute

Configured links: `GW--A (1)`, `A--B (1)`, `GW--B (9)`.

- Lower GW--B to **8**: graph revision advances but the selected tree is still
  GW--A + A--B, total **2**, with no route transitions.
- Lower GW--B again to **0.5**: tree selects GW--B + GW--A, total **1.5**;
  B switches to the direct selected route. Exactly **three** configured
  links remain, and the route transition is explicitly reported.

## 4. Execute and record evidence

From the repository root after extracting Commit 4:

```bash
# Independent Kruskal and cycle/cut proofs (1,216 graph masks).
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshPrimOptimalityAdversarialTests"

# Real sensor registry, actual mesh store, and failure/recovery scenarios.
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MeshPrimSelfHealingAdversarialTests"

# All existing and new Prim tests.
dotnet test SmartX.sln -c Release --filter "FullyQualifiedName~MeshSpanningTree|FullyQualifiedName~MinimumSpanningTree|FullyQualifiedName~MeshPrim"

# Full project acceptance gate (not just topology tests).
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

PR evidence to capture after local execution: test totals, focus-test status,
GitHub Actions run URL, selected edges and costs from the deterministic
fixtures, route classifications after each relay failure, evidence that link
counts remain stable, and the branch's four commit messages. Do **not** invent
actual run counts or use simulated weights as real radio/battery measurements.

## 5. Scope and complexity

An edge-frontier Prim over the gateway component costs O(E log E) heap work
with O(V + E) auxiliary space, plus snapshot sorting/reconciliation performed
by the mesh store. Kruskal and the cycle/cut certificate in this document are
**test-only oracles**, not a second production routing implementation. The
exhaustive enumeration is intentionally limited to tiny graphs; larger-load
measurements remain scoped to P3-11.

The refresh is an explicit read after a topology or sensor-state change. P3-07
will expose versioned mesh, route, and MST data via typed HTTP endpoints and
simulate updates; no background watcher or physical link discovery is claimed.
