# P3-08 Commit 3 — Overlay and Simulation Verification

## Purpose

Verify that the React topology dashboard visualizes **actual server data** and
allows explicitly validated simulation edits without fabricating edges or
optimistically reporting a successful mutation.

## Test fixture: BFS is not Prim

Create two new **Online** sensors: Relay and Probe. Configure these actual
undirected links through `/api/topology/links`:

| Link | Positive relative cost |
| --- | ---: |
| Gateway — Relay | 1.00 |
| Relay — Probe | 1.00 |
| Gateway — Probe | 9.00 |

When Probe is selected, the expected outcomes are:

- **BFS overlay:** Gateway — Probe is highlighted, because it is one hop.
- **Prim overlay:** Gateway — Relay plus Relay — Probe are highlighted, because
  together they have total selected edge cost **2.00**, smaller than choosing
  the direct 9.00 link. This tree route for Probe has two hops.
- **All connections overlay:** All three explicitly configured links appear.
- **Route details:** Separate BFS hop count and MST route accumulated cost are
  visible. These are two different objectives, not interchangeable claims.

The Playwright regression validates real returned link IDs and their
`data-overlay-state` attributes rather than assuming hard-coded local links.

## Simulation failure and recovery

Register an Online sensor and create its only gateway link. Select it in the
mesh dashboard and switch to **BFS / Fewest-hop route**.

| Stage | Backend operation | Expected UI |
| --- | --- | --- |
| Baseline | `POST /api/topology/links` | Selected link highlighted; sensor reachable |
| Failure | `PATCH /api/sensors/{id}/status` to `Offline` | `Unavailable`; no eligible BFS path highlighted; configured physical link remains visible as context |
| Recovery | `PATCH .../status` to `Online` | The same shared selection remains; authoritative path reappears |

This is a logical process-local failure simulation. Recovery can affect
multiple sensors through graph traversal; the backend calculates those routes.

## Safety, error handling and user feedback

- Create: positive finite cost, two distinct registered vertices; server issues
  the link ID. Unknown or duplicate pairs are rejected by backend validation.
- Update: positive finite cost, existing link ID. A server-confirmed cost
  change triggers a new graph-revision-consistent read.
- Delete: explicit browser confirmation, `204 No Content` from server, then
  refetch; the diagram does not erase the link before success.
- Health: existing registered-sensor status API; Offline/Fault/Maintenance
  block forwarding, while configured links remain in storage.
- Access policy: the P3-07 server rejects link writes outside enabled
  simulation environments (`403`). The dashboard exposes this error rather
  than implying the edit succeeded.
- Network error: the client does not know whether a disconnected request was
  committed. It asks the user to refresh before retrying.

## Local proof commands

```bash
cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep 'P3-08 real API mesh routing overlays and simulation controls' --retries=0
CI=true npm run test:e2e -- --grep 'P3-08 accessible interactive mesh visualization' --retries=0
cd ../..
git diff --check
```

Capture actual screens for the final assessment (do not manufacture them):

1. All three configured links and their costs, with Probe selected.
2. One-hop BFS link highlighted and textual summary.
3. Two-branch Prim backbone highlighted and cost total.
4. Sensor Offline, route unavailable, selected sensor still retained.
5. Same sensor restored Online with a refreshed path.

**Status:** Expected behaviour documented for verification; no local Playwright
pass/fail result is claimed in this document.
