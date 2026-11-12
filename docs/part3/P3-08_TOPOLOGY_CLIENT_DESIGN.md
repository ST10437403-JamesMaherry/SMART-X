# P3-08 — Accessible Interactive Mesh Dashboard

**Issue:** `[P3-08] Build the accessible interactive mesh dashboard`

**Branch:** `feature/part3-topology-dashboard`

**Commit 1:** `feat(client): add typed topology APIs and live refresh hooks`

## Objective and scope

Connect the third React pillar to the **actual** P3-07 read-only graph, BFS, Prim
and selected-device endpoints. This commit implements the source-of-truth data
layer and a minimal functional status/selection surface. The logical SVG graph,
node table, route overlays and simulation controls are reserved for subsequent
P3-08 commits; do not claim those features are present yet.

## Exact server contracts

| Client function | Backend endpoint | Response | Source of truth |
| --- | --- | --- | --- |
| `getMeshTopology()` | `GET /api/topology` | `MeshTopologyResponse` | P3-04 synchronized adjacency store plus actual sensor registry |
| `getMeshReachability()` | `GET /api/topology/reachability` | `MeshReachabilityResponse` | P3-05 BFS |
| `getMeshMinimumSpanningTree()` | `GET /api/topology/mst` | `MeshMinimumSpanningTreeResponse` | P3-06 Prim |
| `getMeshDeviceRoute(id)` | `GET /api/topology/devices/{id}/route` | `MeshDeviceRouteResponse` | One graph revision for BFS and Prim selected routes |

TypeScript fields match the .NET records under `SmartX.Contracts.Topology`.
GUIDs, canonical gateway/sensor keys and link IDs are preserved unchanged.
Relative simulated edge weights **are not physical joule or battery data**.

## Why revision consistency matters

The four GETs are separate HTTP requests. If a sensor goes Offline or a link
is removed between them, the server can legitimately return different graph
revisions. Client code must **not** put those projections on screen together.

`loadConsistentTopology()` performs the following:

1. Fetch topology, reachability and MST concurrently.
2. Require matching `graphVersion` and `gatewayKey` for all three results.
3. Fetch the selected registered sensor's route, if selected, and require that
   response to match the same graph revision and sensor ID.
4. Retry at most three times on inconsistent revisions or a disappearing
   selected registration; otherwise display a clear recoverable error.
5. Publish all accepted results as **one React state update**. No partial
   response or fabricated link/path is exposed to the view.

`useTopologyData()` also cancels superseded requests with `AbortController`
and checks a monotonically increasing request generation. A selection change
or SignalR invalidation immediately hides stale results until a consistent
replacement arrives.

## Realtime and stable cross-pillar selection

The existing `useTopologyRealtime()` subscribes to the existing
`/hubs/realtime` SignalR endpoint. It emits refetch requests after topology
and sensor lifecycle messages and after reconnect; ordinary telemetry events
do not cause redundant whole-graph refreshes. **HTTP remains authoritative.**

Selection uses `useWorkspaceState().selectedSensorId`, which is already owned
by the provider above `/ingestion`, `/realtime` and `/topology`. Refreshes
never reset that selection. If the selected sensor is no longer registered,
the UI reports the missing selection without clearing operator context.

## Initial UI behaviour — Commit 1

- Network failure: explicit error plus keyboard-operable Retry button.
- Pending revision check: accessible loading status and no stale graph summary.
- Successful result: real registered-device count, configured-link count,
  BFS reachable/disconnected/unavailable counts and MST total backbone cost.
- Empty mesh: explicit guidance to register sensors and configure links.
- Native sensor picker: real names and physical identifiers; selection
  survives navigation and refetches matching BFS/Prim route information.
- Manual Refresh: refetches the complete consistent projection without
  reloading the document.
- Narrow screens: summary grid collapses; no client-created network map.

## Validation and evidence

Run from `src/SmartX.Client`:

```bash
npm ci
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep "Part 3 live topology API foundation" --retries=0
```

The focused Playwright tests run against the **actual isolated .NET API** and
Vite server, register unique sensor records, verify authoritative results,
retain selection through cross-pillar navigation and check a 375px viewport.
Two explicit browser fault-injection tests exercise inconsistent graph versions
and network failures. The older P3-01 navigation regression is updated to
expect real topology data instead of the removed health-only placeholder.

No screenshot or successful local browser result is asserted by this document
until these commands have actually passed.

## Remaining P3-08 commits

2. `feat(client): render accessible mesh vertices edges and node details`
   — deterministic bounded SVG, accessible node/edge table and search.
3. `feat(client): visualize BFS routes MST backbone and failures`
   — explicitly labelled modes, derived overlays and guarded real simulation
   controls, with live unavailable/disconnected state.
4. `test(e2e): verify responsive network traversal and rerouting UI`
   — full real-API Playwright browser validation, keyboard, mobile, reconnect
   and failure/recovery evidence.
