# P3-07 Commit 3 — Real-time topology invalidation verification

**Issue:** `[P3-07] Expose topology APIs and reproducible network simulations`
**Branch:** `feature/part3-topology-api`
**Scope:** Notification and reconnect contract only. The greenhouse scenario remains Commit 4.

## Source-of-truth and event architecture

The real sensor registry, synchronized mesh store, BFS and Prim implementations
remain unchanged. The established `RealtimeChanged` SignalR event on
`/hubs/realtime` notifies clients only that existing authoritative REST
projections should be refetched. Graph vertices, links, selected MST branches,
paths and simulated costs never come from a SignalR payload.

The read endpoints are:

- `GET /api/topology`
- `GET /api/topology/reachability`
- `GET /api/topology/mst`
- `GET /api/topology/devices/{id}/route`

## Test matrix

| Verification | Expected result |
| --- | --- |
| Successful link creation | One `topology-link-created` after persistence |
| Reversed duplicate or invalid cost | HTTP 400; no link-change event; unchanged revision |
| Changed link weight | One `topology-link-cost-changed`; new graph revision |
| Equal-cost repeat | HTTP 200; same graph revision; no cost-change event |
| Successful link removal | One `topology-link-removed`; correct route disconnection via GET |
| Repeat unknown deletion | HTTP 404; no second event |
| Read-only topology/BFS/MST/route GETs | No new invalidation events |
| Existing registration | `sensor-registered` with real sensor ID |
| Changed explicit sensor status | `sensor-status-changed`; new forwarding state on GET |
| Repeated unchanged status | No additional status-change event |
| Metadata rename | `sensor-updated`; new label even with same graph version |
| Accepted telemetry recovers Offline sensor | Existing `telemetry-changed` plus actual `sensor-status-changed` |
| Subsequent telemetry while Online | No additional topology status event |
| Simulated notifier exception | Authoritative link remains committed and HTTP still reports success |
| Initial hub connection/reconnection | Client callback receives no payload and refetches authoritative GET |

The `RealtimeChanged` contract remains unchanged. Reuse of the existing
SignalR hub avoids competing subscriptions and prevents duplicating graph
state in a notification transport.

## Operational properties

1. Link notification always follows a successful store write.
2. Notification delivery is best-effort and must not fail the underlying
   committed HTTP request.
3. The stable reason names allow the topology hook to ignore unrelated
   telemetry and operator-command traffic.
4. React's selected sensor GUID is not owned or reset by the hook; P3-08
   must preserve it while invalidating and refetching graph projections.
5. Graph versions are checked on fetched HTTP responses, because separate
   GET requests may observe different revisions under concurrent changes.
6. Same-version sensor metadata changes still require a refresh.
7. The hub retry/reconnect mechanism is inherited from `useRealtimeStream`.

## Local verification commands

```bash
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologyRealtimeInvalidationTests"
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologyReadEndpointTests|FullyQualifiedName~MeshTopologySimulationEndpointTests|FullyQualifiedName~MeshTopologyRealtimeInvalidationTests"
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore

git diff --check

cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
```

These are commands to execute locally; this document does **not** claim a
specific count or successful execution prior to the user's acceptance run.

## Remaining boundary

P3-07 Commit 4 will register a deterministic greenhouse via genuine APIs,
create explicit weighted links and test fallback connectivity after a relay
failure. P3-08 will implement the real visual dashboard and subscribe to
the hook created in this commit.
