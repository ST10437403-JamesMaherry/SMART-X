# P3-09 — Cross-pillar selected-device workflows (Commit 2)

## Baseline and scope

- **Authoritative checkpoint:** `dev` merge `6dfe8c23e2ed` (P3-08, PR #69), with P3-09 Commit 1's typed AVL history panel layered on top.
- **Feature branch:** `feature/part3-topology-history`.
- **Commit:** `feat(client): connect topology device details across all three pillars`.
- **Boundary:** This commit changes only React state, route-only UI and browser tests. It does not implement a second telemetry store, client-side routing algorithm, or new actuator command operation.

## Identity and navigation contracts

All three pillars read the same selected sensor ID from `WorkspaceStateProvider`. When the operator follows a shortcut, the link contains only a fixed route and fixed in-page section identifier. A sensor identifier is never copied into a query parameter or replicated to independent pillar stores.

| Source section | Destination | Purpose |
|---|---|---|
| Topology selected node inspector | `#topology-history-heading` | Open the selected sensor's **AVL accepted-telemetry** time-range panel |
| Any selected-device workflow | `/ingestion#sensor-management` | Inspect real registration details and profile selection |
| Any selected-device workflow | `/ingestion#telemetry-management` | Submit or inspect Part 1 **accepted telemetry** |
| Any selected-device workflow | `/realtime#realtime-command-title` | Inspect real actuator commands; execution remains user-confirmed and backend-validated |
| Any selected-device workflow | `/realtime#realtime-history-title` | Inspect **processed** events and accepted commands in the Part 2 operational timeline |
| Other pillars | `/topology#topology-workspace-title` | Inspect the selected device's actual BFS/MST routes |

**Important:** The indexed history includes *accepted* telemetry, including observations not yet processed by the worker. The real-time history uses the existing *processed event* timeline. These two histories have distinct semantics and are never presented as interchangeable.

### Deep links and accessibility

`PillarDeepLinkFocus` observes the exact route hash, accepts only known section IDs and focuses/scrolls the semantic destination when mounted. This handles the existing asynchronous sensor registry and realtime snapshot, including browser Back/Forward. It disconnects the observer on navigation or after ten seconds. Heading focus uses `tabindex="-1"`; an explicit outline is visible, without adding fake buttons or rewriting the accessible heading roles.

The reusable `SelectedSensorLinks` navigation is rendered **only when a registered sensor is present in the current authoritative server projection**. Missing selections show the existing honest unavailable state instead of offering shortcuts to fictitious devices. Clicking a shortcut does not record `DeviceViewed`, execute a relay, start telemetry ingestion, or fabricate a history entry.

## Safe AVL form-draft retention

The `TopologySensorHistory` component now obtains date/time inputs and page size from the existing application-lifetime provider. The bounded dictionary is keyed by lower-cased registered sensor UUID and stores at most **32 recently edited devices**. It contains only:

- `fromLocal` and `toLocal` native datetime-local values;
- a validated-by-UI page-size choice (2, 25, 50 or 100).

No telemetry readings, pagination cursors, command payloads, auth credentials or selected files enter the provider. Actual AVL pages are fetched **only after deliberate Query/Previous/Next actions**. On a device change or route remount, query results are discarded and in-flight requests are aborted; date/time inputs remain available for the next intentional query. A browser refresh starts a new workspace session.

## Verification

Run from `src/SmartX.Client` with the repository's existing Playwright-managed real .NET API:

```bash
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep "P3-09 selected-device cross-pillar workflows" --retries=0
CI=true npm run test:e2e -- --grep "P3-09 AVL-backed per-device history foundation" --retries=0
CI=true npm run test:e2e -- --grep "P3-01 integrated three-pillar navigation" --retries=0
CI=true npm run test:e2e -- --retries=0
```

Tests register fresh sensor identities through the genuine API. Assertions cover deep-link focus into asynchronously mounted sections, provider-owned selected-sensor identity, unchanged document `performance.timeOrigin` during SPA navigation, no synthetic operator interactions, typed telemetry draft retention, Back/Forward, isolated per-sensor AVL drafts and 375px document overflow.

**Do not claim** a test count until the test runner actually reports it. The Playwright suite uses one worker and shares one process-local API, but unique sensor identifiers prevent dependency on a clean registry.

## Follow-up (Commit 3)

Add the large, real-API AVL verification with out-of-order timestamps, same-timestamp buckets, typed values, and narrow range retrieval. Retain the existing complete Part 1/2 browser regression as the merge acceptance gate.
