# P3-07: Typed topology APIs, simulation writes, realtime invalidation and greenhouse regression

**Issue:** `[P3-07] Expose topology APIs and reproducible network simulations`
**Branch:** `feature/part3-topology-api`
**Status:** All four P3-07 implementation stages packaged; final local and CI verification must be recorded before merging.

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
- **Commit 3 (implemented):** Publish link and sensor-status invalidations through the existing real-time hub; reconnect and current-device selection remain a frontend concern for P3-08, supported by a reusable hook.
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


## 7. P3-07 Commit 3: SignalR topology invalidation and reconnect

### Reuse the existing hub; do not create a second network authority

Smart-X already exposes **`/hubs/realtime`** and its `RealtimeChanged` event,
containing `reason`, `sensorProfileId` and `occurredAtUtc`. Commit 3 extends
this lightweight invalidation mechanism. It deliberately does **not** send a
cached topology or routing calculation through SignalR. Only REST GETs are
authoritative; no graph cache, second hub, websocket command handler or
separate sensor-status API has been introduced.

| Source of authoritative change | `RealtimeChanged.reason` | Sensor GUID |
| --- | --- | --- |
| Successful `POST /api/topology/links` | `topology-link-created` | null |
| Successful changed-cost `PATCH /api/topology/links/{id}/cost` | `topology-link-cost-changed` | null |
| Successful `DELETE /api/topology/links/{id}` | `topology-link-removed` | null |
| Existing `POST /api/sensors` | `sensor-registered` | registered sensor |
| Existing `PUT /api/sensors/{id}` | `sensor-updated` | edited sensor |
| Existing changed `PATCH /api/sensors/{id}/status` | `sensor-status-changed` | changed sensor |
| Accepted telemetry promotes Offline to Online | `sensor-status-changed` | revived sensor |

Invalid requests, duplicate links, unknown deletions and equal-cost no-op
PATCHes **do not** produce a new link-change notification under serial
request execution. Telemetry that leaves a sensor's status unchanged continues
to publish the existing ordinary telemetry invalidation, **not** a topology
status event. The notifier is called only after an authoritative mutation.

Graph state and SignalR transport have independent failure semantics. The
server catches publisher failures so an already persisted link is still
reported as a successful HTTP operation even if connected clients cannot
receive the event. This is **best-effort notification**, not guaranteed event
delivery. On reconnect, clients must issue a fresh authoritative GET.

### Frontend subscription contract for P3-08

`src/SmartX.Client/src/features/topology/useTopologyRealtime.ts` provides
`useTopologyRealtime(onInvalidated)` and `shouldRefreshTopology(change)`.
It reuses `useRealtimeStream` with its existing initial-connect retry and
automatic-reconnect handling. A missing payload (`undefined`) means initial
connection or reconnection and **always invalidates**, because messages may
have been missed while disconnected. Other realtime messages (telemetry or
command changes) do not unnecessarily re-query the mesh.

When P3-08 consumes this hook, its callback should:

1. Preserve the selected device's canonical GUID in React state. Do **not**
   clear selection on graph invalidation or hub reconnect.
2. Abort an older in-flight mesh snapshot request and increment a local
   request-generation counter before issuing the next GET.
3. Load graph, BFS and MST results from the authoritative HTTP endpoints.
   Their graph versions must match before combining them in one visual.
4. For the still-selected GUID, refetch its existing device route; show
   explicit `Disconnected` / `Unavailable` or `404` if applicable.
5. Ignore responses from superseded generations and preserve the last valid
   displayed state until a coherent current revision is available.
6. Do not reject same-version sensor **metadata** invalidations: a rename can
   update displayed labels without advancing the graph connectivity version.

The hook prepares this subscription pattern; the actual P3-08 mesh dashboard
and selection-preserving rendering are **not** claimed as completed here.

### Deterministic verification

`tests/SmartX.Api.IntegrationTests/Topology/MeshTopologyRealtimeInvalidationTests.cs`
uses real API registration, real synchronized mesh persistence and real HTTP
mutations. Only the `IRealtimeNotifier` transport is replaced by an
instrumented recording implementation to assert the exact reasons. Coverage
includes link creation, no-op and changed-cost updates, removal, rejected
mutations, read-only GETs, explicit statuses, registry/metadata updates,
telemetry-driven recovery, and simulated SignalR delivery failure.

```bash
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologyRealtimeInvalidationTests"
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

### Limitations and scope

- Topology is process-local and simulated: there is no physical radio control,
  delivery acknowledgement, or persistence across API restarts.
- `GraphVersion` belongs to each GET response, not the SignalR invalidation.
  A quick succession of changes can coalesce into one latest GET.
- Detection of equal-cost no-ops compares the pre-update snapshot with the
  returned link. Under concurrent writers it is advisory; regardless of the
  event count, the next HTTP GET is authoritative.
- P3-08 owns actual view-state updates; P3-07 Commit 4 tests the end-to-end
  greenhouse scenario through the real HTTP pipeline.


## 8. P3-07 Commit 4: Deterministic greenhouse failover and explicit isolation

`tests/SmartX.Api.IntegrationTests/Topology/MeshGreenhouseEndToEndScenarioTests.cs`
registers six actual profiles, creates seven configured links and exercises
real status transitions, GET projections and mutation endpoints. The
initial minimum-cost backbone totals **5.75** simulated units. When the
primary relay fails, an eligible moisture probe uses its configured backup
route with a minimum spanning tree total of **8.25** while a vulnerable
single-bridge probe becomes explicitly **Disconnected**. Removing G--B
and then failing the primary relay yields **zero** reachable sensors and
two reported disconnected components. Restoring the backup link recovers
only the nodes with real eligible connections.

The companion `scripts/demo_part3_greenhouse.py` runs the same scenario
against a fresh, loopback-only Development API and optionally records JSON
evidence with server-issued IDs. See the full fixture, expected results,
command examples and scope boundaries in
`docs/part3/P3-07_GREENHOUSE_SCENARIO_VERIFICATION.md`.

This closes the **P3-07** backend/API issue pending local regression checks
and a reviewed PR into `dev`. The interactive mesh dashboard, API data
fetching in React, selection handling and graph visualization remain **P3-08**.
