# P3-04 / Commits 1–3 — Validated adjacency-list mesh store

**Baseline:** `dev` at `903390efb8557a9c70777dc8d96fd7bca8bd126d` (P3-03 complete) plus P3-04 Commit 1.

## Responsibility and ownership

`InMemoryMeshTopologyStore` provides the **simulated** undirected, weighted network that P3-05 BFS and P3-06 Prim MST will traverse. It does **not** discover real wireless links or operate physical devices.

- `ISensorRepository` remains the **single authority** for registered sensor profile GUIDs and `SensorStatus`.
- A logical gateway vertex is always present and has no `SensorProfile.Id` or `SensorStatus`.
- Each registered sensor is projected to a vertex using `MeshNode.FromSensor(profile)`. Its canonical Smart-X GUID is the graph key; display names and MAC strings do not define graph identity.
- Registration makes a sensor **visible and isolated**. Only an explicit `AddLinkAsync` call configures a simulated connection.
- The singleton store is registered in `src/SmartX.Api/Program.cs`, after the authoritative registry service.

## Data structures

```text
_nodes        Dictionary<MeshNodeKey, MeshNode>
_adjacency    Dictionary<MeshNodeKey, Dictionary<MeshNodeKey, MeshLink>>
_linksById    Dictionary<Guid, MeshLink>
_version      monotonically increasing long
_gate         SemaphoreSlim(1, 1)
```

Each undirected link appears in both adjacency dictionaries, referencing the **same immutable `MeshLink` object**. `_linksById` owns a single canonical link entry for ID lookup and snapshot projection. A mesh of `V` vertices and `E` links uses **O(V + E)** storage.

`ExecuteWithRegistryAsync` obtains the gate **before** the sensor-registry read. This prevents overlapping requests from applying registry projections out of order and guarantees an atomic mesh mutation/snapshot with respect to other mesh operations. The independent sensor registry is not transactionally locked with the mesh; the next mesh call reconciles any subsequent sensor changes.

## Domain and mutation rules

| Operation | Behaviour |
|---|---|
| `GetSnapshotAsync` | Refresh registry, return an immutable snapshot sorted by node key and canonical link endpoints; version stable if nothing changed |
| `GetIncidentLinksAsync(node)` | Return one link per neighbour in deterministic neighbour-key order |
| `AddLinkAsync(link)` | Require registered endpoints or gateway; reject self-links (domain), existing link IDs, duplicate A--B / B--A pairs, and decommissioned endpoints |
| `RemoveLinkAsync(id)` | Remove the ID entry and both adjacency directions; false for an unknown non-empty ID |
| `UpdateLinkCostAsync(id, cost)` | Require an existing link and a finite, strictly positive **simulated** weight; preserve ID/endpoints; do not mutate older snapshots |
| Sensor status transition | Refresh vertex status on next mesh operation and increment graph version; no automatic connection changes except decommissioning |
| Decommissioning | Keep the sensor in inventory with `Decommissioned` status, **remove all its incident links**, and refuse new links |
| Cancellation | Observe cancellation before registry reconciliation and before any requested mutation |

**Offline is not the same as absent.** An Offline sensor may retain an explicitly configured link for diagnostic/simulation purposes, but `MeshNode.IsForwardingEligible` is false. P3-05 and P3-06 must use this property to exclude Offline, Fault, Maintenance and Decommissioned nodes from forwarding paths. Online/Warning nodes and the gateway are eligible; a configured link alone does not prove real-world reachability.

## Version and snapshot guarantees

The version increases only for actual registry vertex/status changes, link additions/removals, and effective cost updates. Calling `GetSnapshotAsync` repeatedly without changes or setting a link to its existing weight does not increment the version.

`MeshGraphSnapshot` validates one gateway, unique vertices, unique link IDs and unordered endpoint pairs, existing endpoints, and no decommissioned links. It copies ordered vertices and links into read-only wrappers. Earlier snapshots remain unchanged after later writes, allowing future BFS/MST algorithms to traverse a stable input revision.

## Cost and complexity (one operation)

The registry refresh scans `V` sensor profiles. Adjacency/ID membership and individual add/remove/cost updates are **expected O(1)** after that scan. Removing a decommissioned vertex's links is **O(degree)**. Snapshot creation and deterministic ordering take **O(V log V + E log E)**; the representation itself stays O(V + E). Neighbour retrieval orders the vertex's degree `d` links in **O(d log d)** after refresh.

The simulated weight is a **relative energy cost**, not a measured watt/joule figure. These methods are an internal domain/application boundary; P3-07 will add guarded simulation mutation endpoints after BFS/MST are implemented.

## Verification

```bash
# From repository root

dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshDomainModelTests"
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~InMemoryMeshTopologyStoreTests|FullyQualifiedName~MeshTopologyAdversarialTests|FullyQualifiedName~MeshTopologyConcurrencyTests"
dotnet test SmartX.sln -c Release
dotnet format SmartX.sln --verify-no-changes
git diff --check
```

The focused tests verify gateway-only startup, authoritative registration and isolation, symmetric link access, duplicate-pair validation, unknown-endpoint rejection, ID removal, immutable cost-update snapshots, deterministic ordering, Offline/Online eligibility, decommissioned link pruning, stable versions, and cancellation.

Commit 3 adds [the complete mesh verification matrix](P3-04_MESH_VERIFICATION.md) and dedicated adversarial/concurrency suites. These independently check disconnected components, dense undirected connectivity, duplicate IDs and pairs, invalid weights, link removal/recreation, status and decommissioning reconciliation, malformed registry recovery, deterministic versions, parallel writers/readers, and cancellation queued behind a deliberately blocked registry read.

**Next:** P3-05 implements actual BFS and gateway reachability. P3-06 implements Prim's heap-based MST and rerouting. No traversal, physical radio, durability or measured performance claims are made by P3-04.
