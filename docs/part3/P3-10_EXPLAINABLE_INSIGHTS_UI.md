# P3-10 Commit 3 — Live insights and learned suggestions in the real UI

## Provenance boundaries

- **Live Network Insights** (Topology and Operations): `GET /api/topology/insights`, read-only rules using the authoritative mesh snapshot and P3-05 BFS. Source fields, graph revision, evaluation timestamp, projection timestamp, first-seen and last-confirmed times, real affected sensor IDs, and explicit operator guidance are displayed. This does **not** claim learned confidence, new radio measurements, or automatic commands.
- **Recent submitted searches** (Operations): `GET /api/realtime/interactions?limit=50`, filtered to real `SearchSubmitted`/`FiltersChanged` events. Recent five submissions and top three criteria are descriptive rankings **within the retained sample**, never model confidence or invented operator behaviour. Counts exclude unseen older events. The `OperatorInteractionRecorded` invalidation refreshes evidence; a passive GET does not record a new event.
- **Suggested Actions** (Operations): Existing `GET /api/realtime/recommendations`. The heading and established `data-testid` selectors remain intact. Each learned recommendation still exposes supporting distinct episodes, heuristic score (not probability), evidence timestamps, source alert, and explicit Review → Confirm. Confirmation rechecks eligibility and calls the unchanged audited command endpoint; it does not run from Live Network Insights.

## HTTP consistency and refresh overhead

- On the Topology page, the insights client is passed the **already-verified** mesh revision and nodes. It asks for the small insights response only, and refuses a different graph version.
- On Operations, there is no topology graph in the existing operations snapshot. The panel fetches `GET /api/topology` and `GET /api/topology/insights` together, and accepts identities only if revisions match (up to three attempts).
- Both panels cancel superseded requests, hide unverified stale conditions and provide a manual retry. A 30-second visible-tab fallback refresh handles transient recovery expiry and missed hub events.
- **No additional SignalR connection:** Topology inherits its existing `useTopologyData` graph revision; Operations extends its existing `useRealtimeStream` handler to refresh insights only for topology-relevant reasons. Ordinary worker telemetry does not re-run an insights GET. The server itself caches BFS computation for the unchanged graph version.
- The panel displays up to five affected sensor identities per insight, while retaining the complete authoritative counts and API omission count. Unknown display names are represented by the **real returned UUID** rather than a fabricated name.

## Accessible interaction / safety

- Native buttons and links, keyboard focus, semantic headings, explicit severity words, and text beyond colour. Mobile layouts stack metadata instead of widening the page.
- Clicking **Inspect route** only selects the actual registered sensor ID in the shared provider and navigates to Topology. It never calls `recordOperatorView`, any operator-training POST, or a relay command endpoint.
- Recovery announcements are explicitly marked historical and time-limited. Unavailable or disconnected devices remain separate current conditions. A node going Offline is **not** a restored path.
- Client and server never assert calibrated predictive probabilities.

## Verification commands (run locally)

```bash
cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep 'P3-10 proactive network insights' --retries=0
CI=true npm run test:e2e -- --grep 'P2-10 genuine learn / suggest / execute / Undo journey' --retries=0
```

Commit 4 adds the real-API browser tests and measured evidence. Do not record unexecuted tests as passing; the initial Part 2 18/30 marker comment has not been supplied.
