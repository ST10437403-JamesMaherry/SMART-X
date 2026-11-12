# P3-09 — Per-device AVL telemetry history

## Commit 1: Typed range endpoint and bounded browser paging

**Baseline:** post-P3-08 merge `6dfe8c23e2ed` on `dev`.
**Feature branch:** `feature/part3-topology-history`.
**Scope:** this commit adds an indexed history panel to Topology without changing the backend, the Part 1 recent-history endpoint, the mesh graph, BFS, Prim, or application-wide state contracts.

### Source of truth

The API route `GET /api/sensors/{sensorProfileId}/telemetry/range` was implemented in P3-03. Its `TelemetryRangePageResponse` is returned by the real per-sensor, per-type AVL secondary index over accepted packets. It is distinct from the Part 1 `GET /api/sensors/{id}/telemetry` ingestion-order *recent* endpoint. The topology client **never** downloads 100 recent readings and filters them locally to pretend they constitute a historical range.

The browser sends inclusive `fromUtc` and `toUtc` instants explicitly in ISO 8601 UTC (`Z`), plus `limit` and (after page 1) the **two-field** continuation `(afterUtc, afterSequence)`. Both cursor fields are mandatory together because the AVL stores equal-timestamp readings in acceptance-order buckets.

The backend accepts page sizes 1 through 1,000; the frontend deliberately caps operator-selected sizes at 100 and also offers a 2-reading mode to expose stable duplicate timestamp pagination during demonstration. Only the **current page** is retained in React state. Returning to an earlier page re-queries the server using its previous small cursor, rather than storing the entire dataset. The UI reports *rows on the current page*, not an invented global count.

### UX decisions

- The history section uses the real shared selected sensor ID from the topology page; it shows name, registered identifier/MAC, UUID, and configured Float/Integer/Boolean type.
- Native `datetime-local` form controls display local time. Before each request, both bounds are converted to UTC ISO strings and the browser rejects missing, unparsable, or reversed bounds.
- The default **draft** range is the previous 24 hours. No request occurs until the operator selects a sensor and explicitly clicks **Query AVL history**.
- Every response is rendered in the **server-provided chronological order**; the client performs no sorting. Equal timestamps appear as separate table rows, not a grouped or deduplicated total.
- Values are displayed from the correct typed field: Float `floatValue`, Integer `integerValue`, Boolean `booleanValue`. `false` and numeric zero are not mistaken for missing values. Values are not rounded.
- Async requests support abort on paging or sensor changes. Failed requests have accessible feedback; empty ranges and pagination end are explicit.
- The history panel is outside the graph's ready/error subtree, so graph refreshes do not remount it. Changing the selected sensor gives it a fresh keyed instance, ensuring previous-sensor readings are not reused.
- The UI uses the established near-black high-contrast theme, semantic labels, table headers, UTC time elements, focus-visible controls, and a horizontally scrollable table rather than document-level overflow.

### Important limitations

- The AVL and telemetry repository remain **process-local**; neither survives an API restart.
- Sequential pages are individually authoritative snapshots, but the server does not promise transaction-level isolation over writes occurring between HTTP requests.
- The index does not expose a global total-count field or a durable per-reading ID. Neither is invented in the client.
- Cross-pillar navigation shortcuts and shared range-draft persistence are explicitly planned for **Commit 2**; larger 10,000+ packet end-to-end evidence is planned for **Commit 3**.

### Focused verification

```bash
cd src/SmartX.Client
npx prettier --write src/api/telemetryRangeApi.ts src/features/topology/TopologySensorHistory.tsx src/features/topology/TopologySensorHistory.css src/pages/TopologyPage.tsx e2e/part3-topology-avl-history.spec.ts
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep "P3-09 AVL-backed per-device history foundation" --retries=0
```

Then run the complete client suite and backend regressions before committing. The Playwright fixture registers real sensors and sends actual HTTP ingestion requests, including out-of-order and equal-timestamp Integer packets, a Boolean `false`, and an empty range. It observes the browser's actual `/range` calls and verifies the server's composite cursor is used for continuation. **Actual pass counts must come from the developer's local execution, not this document.**

### Files changed in this commit

- `src/SmartX.Client/src/api/telemetryRangeApi.ts`
- `src/SmartX.Client/src/features/topology/TopologySensorHistory.tsx`
- `src/SmartX.Client/src/features/topology/TopologySensorHistory.css`
- `src/SmartX.Client/src/pages/TopologyPage.tsx`
- `src/SmartX.Client/e2e/part3-topology-avl-history.spec.ts`
- `docs/part3/P3-09_AVL_HISTORY_DESIGN.md`


## Commit 2 — Shared workflow navigation and safe draft retention

`SelectedSensorLinks` now offers real anchor destinations between the three pillars, with one
`WorkspaceStateProvider.selectedSensorId` remaining authoritative. `PillarDeepLinkFocus`
focuses asynchronously mounted target sections on shortcut navigation and browser Back/Forward.
History form inputs are bounded, per-sensor safe drafts in the workspace provider; AVL rows and
cursors are **not** retained across route unmounts or device changes.

For the full identity, accessibility, and automated verification contracts, see
`docs/part3/P3-09_CROSS_PILLAR_WORKFLOWS.md`.
