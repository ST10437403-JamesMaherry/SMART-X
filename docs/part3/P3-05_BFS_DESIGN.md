# P3-05 - Breadth-first mesh reachability and route tracing

**Issue:** `[P3-05] Add BFS reachability and gateway route tracing`  
**Branch:** `feature/part3-graph-traversal`  
**Dependency:** P3-04 adjacency-list mesh merged into `dev`.

This is the **Commit 1** implementation note. It documents code and repeatable validation commands, not a claim that local tests or GitHub Actions have passed.

## Commit 1: gateway BFS and hop counts

- `MeshTraversalService` reads **one authoritative `IMeshTopologyStore.GetSnapshotAsync`** per request. It never invents sensor registration, wireless links, or radio measurements.
- `CalculateGatewayReachability` operates on an **immutable P3-04 `MeshGraphSnapshot`**, allowing pure, repeatable graph algorithm tests.
- The eligible subgraph contains the logical gateway plus sensors whose `MeshNode.IsForwardingEligible` is `true`: **Online** and **Warning** sensors can relay. **Offline, Fault, Maintenance, and Decommissioned** sensors cannot forward. The underlying snapshot still retains their inventory/status for later classification. P3-04 already removes any incident links on decommissioning.
- Each configured undirected link becomes two adjacency entries only if **both endpoints are forwarding-eligible**. No adjacency is derived from a deployment-tree relationship, physical proximity, or an automatically registered device.
- BFS uses a real `Queue<MeshNodeKey>` for first-in-first-out exploration, a `HashSet<MeshNodeKey>` to prevent revisits/cycles, and a predecessor dictionary for the Commit 2 route builder. The first discovery gives the fewest number of links to the gateway.
- The input snapshot sorts undirected links by canonical `(NodeA, NodeB)` keys. Appending both endpoints' neighbours in that order yields neighbours in ascending identity order **without another sort**. Results iterate `snapshot.Nodes`, which is also sorted. This gives deterministic tie ordering in O(V + E) traversal work.
- Each `MeshGatewayReachability` result includes the originating graph version, sorted **defensive/read-only** reachable node/hop records, a sensor count, and a `TryGetHopCount` lookup. The gateway is always present with zero hops; an unlinked or unavailable sensor is not reported as reachable.

### Complexity

For **V** registered vertices including the gateway and **E** explicitly configured undirected links, eligible-adjacency construction and BFS together take **O(V + E)** time and **O(V + E)** additional memory. The topology store's earlier registry reconciliation and snapshot ordering are separate operations and are not hidden within this BFS bound. Hop counts deliberately ignore `SimulatedEnergyCost` - BFS minimises links, whereas the P3-06 Prim algorithm minimises the tree's total simulated cost.

## Remaining issue work

- **Commit 2:** `feat(topology): reconstruct per-device paths and disconnected components` - build ordered sensor-to-gateway predecessors and separate physically disconnected sensors from devices unavailable due to health; expose explicit unreachable classifications.
- **Commit 3:** `test(topology): verify BFS paths cycles and offline forwarding` - add adversarial equal-hop diamonds, cycles, alternate routes, stale-state changes, invalid keys and independently verified graph fixtures.

The BFS service is registered with dependency injection but **does not add any HTTP endpoints or UI simulations yet**. Typed topology APIs and permitted simulation commands belong to P3-07.

## Verification (run locally, do not assume green)

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshGatewayReachabilityTests"

dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshTraversalServiceTests"

dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

Record actual test totals and the CI link in the final P3-05 PR. Do not claim benchmark gains or measured radio latency from this algorithm.
