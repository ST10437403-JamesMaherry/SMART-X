# P3-07: Typed mesh topology API — Commit 1

**Issue:** `[P3-07] Expose topology APIs and reproducible network simulations`
**Branch:** `feature/part3-topology-api`
**Status:** Read-only API foundation; simulation writes, SignalR invalidation, and end-to-end demonstrations follow in Commits 2–4.

## 1. Contract surface

| HTTP | Path | Purpose |
| --- | --- | --- |
| GET | `/api/topology` | Current gateway and registered sensor vertices, true configured links and their positive simulated costs |
| GET | `/api/topology/reachability` | P3-05 BFS hop counts, disconnected components, and unavailable sensors |
| GET | `/api/topology/mst` | P3-06 Prim backbone edges, total selected cost, connectivity summary |
| GET | `/api/topology/devices/{id}/route` | Same-revision BFS and MST route for a registered sensor |
| GET | `/api/sensors/{id}/telemetry/range?fromUtc=...&toUtc=...` | **Existing P3-03** AVL-backed, paginated sensor history; no duplicate topology index |

The API uses typed `SmartX.Contracts.Topology` responses. All mesh node keys are canonical strings: `gateway` or `sensor/{lowercase-guid}`. Sensor vertices contain the **registered** sensor GUID, name, MAC/unique device identifier, category, operational status, telemetry data type and deployment location. The gateway is a distinct logical vertex with null sensor metadata. Link IDs, endpoints and weights come from the real adjacency-list store; the API creates no edges.

## 2. Status semantics and paths

- `Reachable`: an eligible sensor has a configured route to the gateway.
- `Disconnected`: the sensor can forward, but its component has no eligible path to the gateway.
- `Unavailable`: the registered sensor is Offline, Fault, Maintenance or Decommissioned; it is inventoried but not eligible to forward.
- Unknown sensor GUID: HTTP **404** RFC ProblemDetails. Empty sensor GUID: HTTP **400** RFC ProblemDetails.

The route response contains **two explicit, different algorithms**. `Bfs.PathToGateway` is the shortest-hop route (ignores energy weights). `Mst.PathToGateway` follows the selected Prim backbone; `Mst.AccumulatedSimulatedEnergyCost` sums its selected route links. The full MST response's `TotalSimulatedEnergyCost` is the **sum of all selected backbone edges**. A minimum-cost spanning tree does not necessarily provide a minimum-cost route for each individual sensor.

Paths list sensor-to-gateway vertices. For disconnected and unavailable sensors, both paths are empty, the hop counts are null, and the tree route cost is null; no false partial route is returned.

## 3. Snapshot and lifetime guarantees

The existing `IMeshTopologyStore` returns an immutable graph revision. Reachability and MST route calculations for the selected sensor consume **that same snapshot**. Results from separate HTTP requests can legitimately differ in `GraphVersion` when the sensor registry or link store changes concurrently. A client must therefore check versions when composing an interactive dashboard, refresh from the API after topology invalidation, and avoid combining mismatched revisions.

A topology GET reads real registry metadata after the authoritative graph snapshot. The sensor **status** is taken from the graph snapshot (not a later registry read), preserving one routing-state revision. All results are newly mapped transport values, not references to mutable adjacency dictionaries. Registration and link state remain process-local in this demonstration; restarting the API resets the simulated mesh.

## 4. Reproducible read-only proof

`tests/SmartX.Api.IntegrationTests/Topology/MeshTopologyReadEndpointTests.cs` uses real HTTP registration and status endpoints, `WebApplicationFactory<Program>`, and the actual `IMeshTopologyStore` to preconfigure test links. It checks:

1. An empty registry returns **only** the gateway, with no fabricated edges.
2. Newly registered sensors expose genuine metadata, start Offline, and remain isolated until explicitly linked.
3. A configured GW–relay–leaf graph returns consistent revisions, exactly two links, correct BFS paths and correct weighted MST costs.
4. A triangular graph demonstrates the distinction between the fewest-hop BFS path and the cheaper selected Prim tree path.
5. An Offline relay is still inventoried with configured links, while its downstream leaf becomes explicitly disconnected.
6. Unknown sensor IDs yield 404 and empty GUIDs yield 400 using existing ProblemDetails middleware.
7. The existing AVL historical range endpoint remains available for selected sensors.

Run:

```bash
# Execute focused tests.
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologyReadEndpointTests"

# Run the standard full regression gates.
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

## 5. Next commits

- **Commit 2:** Strictly validated link addition, removal and cost updates behind an explicit simulation-only gate. Reuse `IMeshTopologyStore` rather than creating competing topology state.
- **Commit 3:** Publish topology invalidation reasons through the existing real-time pattern, including sensor health/status transitions; clients still use authoritative GET after invalidation/reconnect.
- **Commit 4:** Deterministic, actual-API greenhouse demo and integration regression proving alternate paths or explicit disconnection after relay failure.

No endpoint in Commit 1 edits the network or claims to control physical IoT radio hardware.
