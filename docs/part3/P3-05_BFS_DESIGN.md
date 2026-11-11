# P3-05 — Breadth-first mesh reachability and route tracing

**GitHub issue:** `[P3-05] Add BFS reachability and gateway route tracing`  
**Branch:** `feature/part3-graph-traversal`  
**Dependency:** P3-04 — validated adjacency-list mesh, merged into `dev` before this feature branch.

This note records implementation contracts and reproducible checks; it is not evidence that a test command or GitHub Actions ran. The code in this commit does not discover real radio links or calculate an energy-optimal route.

## Commit 1 — Gateway reachability and fewest-hop counts

- `MeshTraversalService.GetGatewayReachabilityAsync()` reads one authoritative `IMeshTopologyStore.GetSnapshotAsync()` per operation. It never creates sensor registrations, wireless links or measured radio costs.
- `CalculateGatewayReachability()` is a pure computation over an immutable `MeshGraphSnapshot`. Every reachable vertex is reported with its fewest **number of configured links** from the gateway; the gateway is included at zero hops.
- Each undirected link is added to two temporary adjacency lists only if **both endpoints** are forwarding-eligible. The gateway is always eligible; Online and Warning sensors may relay. Offline, Fault, Maintenance and Decommissioned sensors cannot.
- BFS uses `Queue<MeshNodeKey>` for first-in-first-out traversal, `HashSet<MeshNodeKey>` to avoid revisiting vertices and a predecessor dictionary for the first-discovered (fewest-hop) route.
- Nodes and links are canonically sorted by P3-04. This produces ascending neighbour ordering without an extra sort, so an equal-hop tie follows a deterministic parent rather than hash iteration order.
- `MeshGatewayReachability` is a defensively copied, immutable, graph-versioned hop-count projection. It cannot imply a route to an unlinked or unavailable device.

## Commit 2 — Per-sensor routes and disconnected components

### Public application operations

| Operation | Purpose |
| --- | --- |
| `GetGatewayRoutingAsync(CancellationToken)` | Read exactly one reconciled snapshot; classify every registered sensor and its eligible disconnected components |
| `GetSensorRouteAsync(Guid, CancellationToken)` | Retrieve one immutable sensor-to-gateway route, or `null` if the sensor is not registered; `Guid.Empty` is invalid |
| `CalculateGatewayRouting(MeshGraphSnapshot, CancellationToken)` | Pure, deterministic BFS and component classification for unit testing and future API integration |
| Existing `GetGatewayReachabilityAsync` / `CalculateGatewayReachability` | Still supported for clients needing only gateway hop counts |

### Route outcomes

| `MeshRouteStatus` | Meaning | `PathToGateway` | `HopCount` |
| --- | --- | --- | --- |
| `Reachable` | The registered sensor has an eligible path to the gateway | Full path, **sensor first / gateway last** | Number of links (>= 1) |
| `Disconnected` | Sensor can forward but its eligible component has no gateway path | Empty | `null` |
| `Unavailable` | Sensor is registered but Offline, Fault, Maintenance or Decommissioned | Empty | `null` |
| Unregistered (not an enum status) | No current `SensorProfile` exists with that key | No route object: `TryGetRouteToGateway` returns false and `GetSensorRouteAsync` returns `null` | Not applicable |

The `Unavailable` classification is deliberately separate from a healthy but disconnected sensor. A configured link that passes through an Offline device cannot be used by BFS, and the downstream eligible island is classified `Disconnected`; the underlying link is not silently deleted. The graph store already removes decommissioned incident links.

`MeshGatewayRouting` publishes a read-only list of all registered sensor keys, unavailable sensors, gateway reachability, and **ordered** disconnected components containing only forwarding-eligible registered sensors. These categories partition every registered sensor exactly once. Components are ordered by their smallest sensor key; their members are ordered by key. No invented physical topology is created.

`TryGetRouteToGateway(sensor, out route)` follows the privately copied BFS predecessor map only when a route is requested. It produces the exact vertex sequence from that sensor through its parents **toward the gateway**, never gateway-to-sensor. An unavailable or disconnected sensor has an explicit result with an empty path, rather than a misleading partial route. Callers cannot mutate the returned path, component membership or predecessor map.

### Complexity

Let **V** be the snapshot's vertices (including the gateway) and **E** its configured, undirected links.

- Build eligible adjacency and run gateway BFS: **O(V + E)** time and **O(V + E)** memory.
- Discover remaining eligible components using a second bounded BFS and create sorted projections by scanning the already sorted snapshot: **O(V + E)** total additional work and **O(V)** classification memory.
- Reconstruct a selected sensor path: **O(h)** time and **O(h)** output memory, where h is its hop count. Full routes are not eagerly materialized for every sensor, preventing quadratic path storage in a long chain.
- The P3-04 store's authoritative registry reconciliation and snapshot sorting occur **before** traversal and are excluded from the algorithm-only bound.
- BFS minimizes **hop count**, not total `SimulatedEnergyCost`. Prim's MST in P3-06 minimizes the **sum of selected tree link costs**, and an MST is not a universal least-cost point-to-point path.

### Deterministic example

For configured links `GW—A`, `A—B`, `B—C`, and Online sensors A/B/C, the route for C is `C → B → A → GW` (3 hops). If B becomes Offline, the link configuration may remain, but B is `Unavailable`, and C is `Disconnected` unless another eligible path exists. The returned graph version identifies the immutable snapshot used to calculate each result.

## Tests and next commit

Commit 1 added focused domain and application tests for the immutable gateway hop-count contract and queue-based reachability. Commit 2 adds:

- `tests/SmartX.Domain.Tests/Topology/MeshRoutingContractTests.cs` — immutable route and disconnected-component validation, status semantics, rejection of malformed predecessor maps and defensive-copy checks.
- `tests/SmartX.Application.Tests/Topology/MeshGatewayRoutingServiceTests.cs` — one-hop and multihop paths; disconnected islands, isolated vertices and health-blocked bridges; equal-hop tie ordering; weights ignored by BFS; unknown IDs; immutable snapshot refresh and cancellation.

**Commit 3:** add deeper adversarial traversal tests, including cyclic meshes, competing equal-hop alternatives, bridge failures, duplicate-neighbour resistance and no stale routes after state transitions. No topology HTTP endpoint or dashboard implementation is introduced until P3-07/P3-08.

## Verification — run locally from the repository root

```bash
# Commit 2 route/domain contracts
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshRoutingContractTests"

# Commit 2 path and component calculations
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshGatewayRoutingServiceTests"

# Entire P3-05 domain/application test area, including Commit 1
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshGatewayReachabilityTests|FullyQualifiedName~MeshRoutingContractTests"
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshTraversalServiceTests|FullyQualifiedName~MeshGatewayRoutingServiceTests"

# Full code health / regression
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

Record actual local test results and the CI URL in the final P3-05 PR. Neither timings nor real-world energy savings should be inferred from these simulated test graphs.
