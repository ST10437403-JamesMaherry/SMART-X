# P3-05 — BFS route-tracing regression and verification

**Issue:** `[P3-05] Add BFS reachability and gateway route tracing`  
**Branch:** `feature/part3-graph-traversal`  
**Dependency:** P3-04 adjacency-list mesh graph  
**Scope:** BFS, route reconstruction, component classification, graph revision consistency

This document specifies reproducible tests and expected behaviour. It does **not**
claim that tests, CI, or benchmarks were run in the current environment. Record
actual results in the pull request after executing the commands locally.

## 1. Algorithm and correctness contracts

| Requirement | Production mechanism | Verification |
| --- | --- | --- |
| Real breadth-first traversal | `Queue<MeshNodeKey>` plus `HashSet<MeshNodeKey>` in `MeshTraversalService` | Overlapping cycles, long chain, fixed-seed oracle |
| Minimal hop count | First discovery sets BFS parent and hop distance | Exact chain paths and independent edge-relaxation oracle |
| Deterministic tie-break | Canonical P3-04 link ordering and ascending adjacent keys | Layered diamond, reversed edge orientation/input order, redundant live routes |
| Correct route direction | Route reconstructed from sensor through predecessors to gateway | Exact expected node sequences; every hop verified against an existing link |
| Correct health policy | Only Gateway, Online, Warning are forwarding-eligible | Offline, Fault and Maintenance theories; Warning and decommissioned live cases |
| Failure of a single bridge | Blocked vertex excluded from traversable graph | Hand-drawn articulation fixture and live-store bridge failure |
| Redundant route | BFS discovers a still-eligible alternate path | Immutable snapshot and real sensor-store failover/restoration fixtures |
| No false self-healing | No fabricated links or routes | Removal of the only gateway bridge reports `Disconnected` |
| Distinguish sensor health from missing connectivity | `Unavailable`, `Disconnected`, `Reachable` | Live registration, changed operational status, separate islands |
| Preserve genuine registration | Mesh store references authoritative `ISensorRepository` | Newly registered sensor remains isolated until a link is configured |
| Handle disconnected cycles without revisits | Component scan has its own visited membership | Multiple cyclic islands and isolated sensors |
| Immutable versioned results | `MeshGraphSnapshot`, `MeshGatewayRouting`, `MeshRoute` | Previous route unchanged after version and status change |
| No energy-weight confusion | BFS ignores `SimulatedEnergyCost` | Inverted energy weights preserve equal-hop tie results |
| Concurrent graph changes | Reconciled snapshot read under mesh store gate | Parallel add/remove and routing reads; each result self-consistent |
| Invalid duplicate links rejected | P3-04 `MeshGraphSnapshot` pair validation | Mirrored pair with different IDs rejected before traversal |

## 2. Reproducible graph fixtures

The application tests provide small hand-verified graph shapes:

1. **Overlapping cycles:** `GW—A`, triangles and cross-links among A–E.
   The last device has a three-hop route and is visited once.
2. **Two-layer diamond:** two equal-hop gateway alternatives and two
   equal-hop paths at the next layer. Identity ordering selects the same
   parent regardless of link insertion order or simulated energy weights.
3. **Offline shortcut:** a low-energy two-hop shortcut is unusable when its
   intermediate sensor is Offline. BFS follows the longer eligible route.
4. **Articulation failure:** an Offline gateway-side bridge makes the
   downstream two-sensor island `Disconnected`; the bridge itself is
   `Unavailable`.
5. **Disconnected islands:** a three-sensor cycle, a two-sensor component,
   an isolated sensor and an unavailable sensor are reported separately.
6. **Long chain:** 128 sensor hops produce a 129-vertex path with no cycles.
7. **Fixed-seed cross-check:** 24 eight-sensor graphs use seeded edge and
   sensor-state assignments. Expected hop counts are computed independently
   by repeated edge relaxation, not by copying the production queue BFS.
   Each successful route is also checked for real link adjacency, simple
   vertices, sensor-first ordering and strictly decreasing hop distances.

## 3. Live registry and topology integration

`tests/SmartX.Infrastructure.Tests/Topology/MeshBfsLiveTopologyRegressionTests.cs`
uses `MeshTopologyScenario` from P3-04. This fixture constructs the real
`InMemorySensorRepository` and `InMemoryMeshTopologyStore` and registers real
`SensorProfile` instances. It does not fabricate sensor identities inside
application services or replace the production graph implementation.

The cases cover registration without implicit links; activation; bridge
failure/recovery; equal-hop failover to an alternate eligible bridge; no
fallback when the only gateway edge is removed; graph cost updates; terminal
decommissioning and link cleanup; adding a later unrelated sensor; and
bounded parallel graph churn with self-consistent route snapshots.

Concurrent reads intentionally validate one `MeshGatewayRouting` result
at a time. They do **not** compare two independent snapshot/adjacency reads
as if they were one transaction while another task is writing.

## 4. Validation commands

Run from the root of the Smart-X repository:

```bash
# New pure-algorithm / independent-oracle regression tests.
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshBfsAdversarialRegressionTests"

# New tests against the real registered-sensor repository and mesh store.
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MeshBfsLiveTopologyRegressionTests"

# Run ALL P3-05 algorithm tests, including Commits 1 and 2.
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshTraversalServiceTests|FullyQualifiedName~MeshGatewayRoutingServiceTests|FullyQualifiedName~MeshBfsAdversarialRegressionTests"

# The full solution remains the final regression gate.
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

**Record after running:** actual total tests, failures/skips, CI URL, and the
commit hash. Do not infer performance or radio battery-life improvements from
unit tests. The graph and its weights remain a **simulation**.

## 5. Complexity and scope boundaries

For a constructed, immutable graph snapshot with **V** vertices and **E**
undirected links, building forwarding-eligible adjacency, BFS and component
classification require **O(V + E)** time and auxiliary memory. Predecessor
path reconstruction takes **O(h)** for **h** links. P3-04's inventory
reconciliation and deterministic snapshot sorting occur before BFS and are
not part of the pure traversal complexity bound.

P3-05 finds **fewest-hop routes**, not least-weight paths. P3-06 will use
`PriorityQueue<TElement, TPriority>` for Prim's **minimum spanning tree**;
that tree minimizes the sum of chosen backbone weights, not every individual
source-to-gateway path's weighted cost. P3-07 will expose topology APIs and
reproducible simulations. P3-08 will render the dashboard.

## 6. PR completion checklist

- [ ] P3-05 Commit 1, Commit 2 and Commit 3 are present on the feature branch.
- [ ] Focused tests and full `.NET 10` solution tests pass locally.
- [ ] `dotnet format --verify-no-changes` passes with no final-newline errors.
- [ ] `git diff --check` and `git status --short` show no unintended files.
- [ ] CI passes on the PR and the base branch is `dev`, **not** `main`.
- [ ] PR evidence quotes actual results and distinguishes simulated weights
      from measured radio energy.
