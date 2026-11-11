# P3-07: Typed topology API and simulation writes — Commits 1-2

**Issue:** `[P3-07] Expose topology APIs and reproducible network simulations`
**Branch:** `feature/part3-topology-api`
**Status:** Commits 1–2 provide read-only topology API and gated simulation link writes; SignalR invalidation and the deterministic greenhouse demonstration follow in Commits 3–4.

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

- **Commit 2 (implemented):** Strictly validated link addition, removal and cost updates behind an explicit simulation-only gate. Reuses `IMeshTopologyStore` rather than creating competing topology state.
- **Commit 3:** Publish topology invalidation reasons through the existing real-time pattern, including sensor health/status transitions; clients still use authoritative GET after invalidation/reconnect.
- **Commit 4:** Deterministic, actual-API greenhouse demo and integration regression proving alternate paths or explicit disconnection after relay failure.

No endpoint in Commit 1 edits the network or claims to control physical IoT radio hardware.


## 6. P3-07 Commit 2: Simulation-only link mutations

Mesh links are **configured logical simulation edges**, never discovered or
controlled physical radio links. New devices still come from the existing
`POST /api/sensors` registration API. Sensor health continues to use the
existing `PATCH /api/sensors/{id}/status` endpoint; there is no duplicate
sensor-status controller or second source of truth.

| HTTP | Path | Body | Success | Error conditions |
| --- | --- | --- | --- | --- |
| POST | `/api/topology/links` | `{ "nodeAKey": "gateway", "nodeBKey": "sensor/{registered-guid}", "simulatedEnergyCost": 1.25 }` | `201`, canonical `MeshLinkResponse` | `400` malformed/stale/nonregistered endpoint, self-link, duplicate undirected pair, decommissioned endpoint or invalid cost; `403` when disabled |
| PATCH | `/api/topology/links/{linkId}/cost` | `{ "simulatedEnergyCost": 2.5 }` | `200`, same link ID/endpoints and updated cost | `400` invalid GUID/cost; `404` unknown ID; `403` when disabled |
| DELETE | `/api/topology/links/{linkId}` | none | `204`, no response body | `400` empty GUID; `404` unknown ID; `403` when disabled |

Only canonical `gateway` and `sensor/{guid:D}` node keys are accepted. The
server creates a fresh link UUID rather than accepting caller-selected link
IDs. A reversed endpoint pair is considered the **same undirected link**.
The existing in-memory adjacency store enforces uniqueness, registration,
endpoint eligibility and atomic mirrored adjacency updates under its gate.

The simulation store permits an **Offline** sensor to have a configured link,
but BFS/Prim cannot forward through it until the **existing sensor status API**
reports Online or Warning. A **Decommissioned** sensor cannot be linked; its
previous links are purged when graph state reconciles the registry.

### Environment gate and Docker opt-in

The `MeshSimulationAccessPolicy` uses one explicit rule:

- `Development` and `Test` environment: write operations enabled for local
  development or integration testing.
- Other environments (including default Docker `Production`): writes return
  **403 RFC ProblemDetails** unless `Topology:Simulation:Enabled=true`.
- The normal application config contains `Topology:Simulation:Enabled=false`.
  The Compose API maps
  `Topology__Simulation__Enabled: ${SMARTX_TOPOLOGY_SIMULATION_ENABLED:-false}`.
  To run a **trusted local Docker demo only**, set
  `SMARTX_TOPOLOGY_SIMULATION_ENABLED=true` in the ignored root `.env`.

This gate is **not user authentication**. An enabled demo must remain restricted
to a trusted environment. No Smart-X endpoint in this issue operates physical
radio transmitters, actuators or actual building infrastructure.

### Version semantics and authoritative reads

Successful add/remove/changed-cost operations increment `GraphVersion` exactly
once. Repeating the same link cost is a no-op: it preserves ID, endpoint keys,
link count and graph version. Rejected requests do not mutate the configured
link dictionary. After a successful mutation, obtain fresh authoritative
`GET /api/topology`, `GET /api/topology/reachability`, `GET /api/topology/mst`,
and selected-device routes. SignalR invalidation is deliberately scheduled for
**Commit 3**; this commit does not claim immediate push notification.

`PATCH /cost` first checks that the link was present in a snapshot and then
uses the synchronized store as the final update authority. Under concurrent
link deletion a store-level race can still return domain validation rather
than the precheck's 404; no partial edge or stale algorithm cache is created.

### Integration tests

`MeshTopologySimulationEndpointTests` exercises the actual ASP.NET pipeline,
registered sensor API and real synchronized mesh store:

1. Create gateway-to-relay and relay-to-leaf edges by HTTP; confirm exact
   graph revisions, BFS reachability and weighted MST paths.
2. Reject reversed duplicates, self-links, fabricated or malformed sensor
   keys, invalid costs and nonregistered/stale device identifiers.
3. Retain configured Offline links, make routing eligible through the existing
   status endpoint, and reject decommissioned endpoints.
4. Patch one competing link cost, retain its ID, observe a different MST total,
   and confirm repeating the same cost does not advance revision.
5. Delete an edge and observe real disconnection; unknown/empty link IDs return
   typed 404/400 ProblemDetails.
6. Verify **every** mutation returns 403 in Production without opt-in, and
   Production permits an explicitly enabled local demo.

Focused verification:

```bash
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologySimulationEndpointTests"
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```
