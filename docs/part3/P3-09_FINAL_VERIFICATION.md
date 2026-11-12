# P3-09 — Final AVL history and cross-pillar verification

**Issue:** P3-09 — Add per-device AVL history and complete cross-pillar workflows
**Feature branch:** `feature/part3-topology-history`
**Third commit:** `test(e2e): prove chronological ranges and cross-pillar state`
**Baseline:** `dev` at `6dfe8c23e2ed` (post-P3-08 merge, PR #69), plus green P3-09 Commits 1–2.

This document is a **reproducible verification procedure**, not a claim that
all commands have already executed. Record actual green results only after
running them locally and checking GitHub Actions.

## 1. Architecture and authoritative data boundaries

- `POST /api/sensors` registers each test's real Float, Integer, or Boolean sensor. One case uses a validated, uppercase 48-bit MAC address; sensors use unique UUIDs so the test suite can share one in-memory API process.
- `POST /api/sensors/{id}/telemetry/batches` accepts real jagged telemetry data. P3-03 stores accepted packets in the per-sensor, per-type AVL timestamp index while preserving original ingestion order separately.
- `GET /api/sensors/{id}/telemetry/range?fromUtc=...&toUtc=...&limit=...` returns **inclusive chronological** pages; if `hasMore` is true, callers must replay **both** `afterUtc` and `afterSequence` to continue correctly through duplicate timestamps.
- The older `GET /api/sensors/{id}/telemetry` is the Part 1 **recent ingestion-order** endpoint, not a replacement for historical range traversal. The browser tests explicitly observe `/range` calls.
- `TopologySensorHistory` stores at most one page of server-returned readings. Only small cursor references are kept for Previous/Next. It does not fetch the 10,002-row collection into React or claim a total count it cannot know.
- `WorkspaceStateProvider` owns the single selected sensor UUID and bounded safe form drafts across the Topology, Ingestion, and Realtime pillars. Navigation does not actuate relays or create false interaction episodes.

## 2. Routine browser tests (run on every regression)

File: `src/SmartX.Client/e2e/part3-topology-history-final.spec.ts`.

| Case | Server-side setup | Browser/API assertions |
|---|---|---|
| Chronological Float and duplicate timestamps | Seven genuine readings supplied in nonchronological batches; three share one UTC minute | Every original typed value appears in timestamp order. 2-row pages cross duplicate buckets without lost/duplicated readings. Previous retrieves the earlier page. A one-minute inclusive query returns only the three equal-time readings. No recent-history request masquerades as a range result. |
| Typed-value and identity isolation | Separate Integer and Boolean sensors | Integer `0`, `-42`, Boolean `false`, `true` retain their exact typed representations; changing selection clears the old sensor's results. |
| Search by registered MAC and navigation | One MAC-address sensor plus another registration | Mesh search finds the real MAC. Selecting the semantic table row updates shared identity; the indexed history uses that identity. Ingestion navigation and browser Back retain the sensor and safe time-range draft. |
| Invalid cursor/range limits | A dedicated Integer sensor with two accepted readings | Missing cursor half, negative sequence, reversed bounds, and limit above the backend maximum yield HTTP 400. A subsequent valid indexed read still returns both original readings. |

The existing Commit 1 and Commit 2 suites continue to provide coverage for
empty indexed ranges, mobile viewport, invalid local input, accessible deep
links, device-local drafts, command preview safety, and Back/Forward behavior.

## 3. Opt-in 10,002-reading real API demonstration

File: `src/SmartX.Client/e2e/part3-avl-large-dataset.spec.ts`.

The large dataset is **opt-in**, avoiding unnecessary 10k+ ingestion in every
full browser suite. The test still uses the same Playwright-managed ASP.NET Core
API and an actual registered Integer sensor; it does not mock the range route.

Deterministic dataset construction:

1. Register one fresh Integer sensor with a UUID and unique identifier.
2. Submit 10,000 distinct minute-separated packets **in descending timestamp order**, in batches of at most 250 readings. A checked HTTP 201 and `acceptedPacketCount` must be returned for each batch.
3. Append **two extra** readings at the same timestamp as ordinal 5,000, with values `-701` and `-702`. The original reading at that timestamp has value `5000`.
4. Assert the acknowledged total is **10,002** packets (no accepted-count estimates).
5. Traverse the full inclusive range using server pages capped at 1,000 rows and the composite cursor. Assert **exactly 10,002 indexed rows** and nondecreasing timestamps, without accumulating the rows in React state.
6. Request only the exact shared-minute range with `limit=2`. Expect `[5000, -701]`, then `[-702]` on the continuation page. That proves inclusive range semantics and duplicate-bucket acceptance order.
7. Open Topology, select the registered sensor and issue the same narrow history query via the actual UI. Assert the two displayed pages contain the same three values and generate only two `/telemetry/range` browser requests.
8. Attach the actual per-run verification summary (including number accepted, indexed rows, narrow results and elapsed milliseconds) to the Playwright report as `p309-avl-10002-verification.json`.

**Important performance qualification:** The elapsed time is descriptive of
that run's local machine and test server, **not** a benchmark of AVL `O(log n + k)`
independent of HTTP, batch persistence, notifications, and browser operations.
Formal p50/p95 range-query benchmarks with controlled repetition belong in
P3-11. Do not present an estimated execution time as a measured result.

## 4. Commands for the developer

From the repository root:

```bash
cd src/SmartX.Client

npx prettier --write \
  e2e/part3-topology-history-final.spec.ts \
  e2e/part3-avl-large-dataset.spec.ts

npm run lint
npm run format:check
npm run build

# Four new, quick real-API scenarios:
CI=true npm run test:e2e -- \
  --grep "P3-09 final chronological AVL and cross-pillar regression" --retries=0

# Existing P3-09 foundations:
CI=true npm run test:e2e -- \
  --grep "P3-09 AVL-backed per-device history foundation" --retries=0
CI=true npm run test:e2e -- \
  --grep "P3-09 selected-device cross-pillar workflows" --retries=0

# Full Parts 1/2/3 suite; the expensive 10k case is intentionally skipped:
CI=true npm run test:e2e -- --retries=0

# Opt-in real dataset evidence: isolated run with no retries:
SMARTX_P309_LARGE_DATASET=1 CI=true npm run test:e2e -- \
  --grep "P3-09 opt-in 10002-reading AVL evidence" --retries=0

cd ../..
git diff --check
```

The opt-in test includes a higher test timeout for the heavy data volume, but
has a hard per-request timeout and rejects any missing/incorrect acceptance
receipt. **Do not run it against a production service.** The Playwright fixture
uses an isolated Development-mode API. Because the repository is in-memory,
all data created for that run is lost when its test server exits.

Backend safety gates (especially important when closing the issue):

```bash
dotnet restore SmartX.sln
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release --no-build
dotnet format SmartX.sln --verify-no-changes --no-restore
```

Record the real output; the checkboxes below are intentionally left open
until observed.

## 5. Evidence collection and rubric mapping

| Rubric focus | Evidence to capture |
|---|---|
| AVL tree index (30-mark area) | Playwright summary shows 10,002 accepted packets and 10,002 indexed rows. Show the single-minute 3-row query and continuation cursor from the actual API. |
| Original typed data | UI screenshot for an Integer zero/negative, Boolean false/true, Float decimal and three equal-timestamp values. |
| Bounded frontend memory | Two-row current page, no full-history download, next/previous cursor requests in browser Network tab. |
| Full integration (5-mark area) | Search by actual MAC; selected UUID appears in history and remains selected after Ingestion/Realtime navigation and Back. |
| No regressions | Full Playwright run, backend build/test/format, and GitHub Actions green. |

Suggested filenames under a later `docs/evidence/part3/` evidence commit
(only after screenshots and logs truly exist): `p309-avl-10002-api.png`,
`p309-narrow-duplicate-history.png`,
`p309-cross-pillar-mac-selection.png`, and
`p309-local-validation.txt`. These filenames are **suggestions**, not files
included or results already captured by this change. P3-11 is the designated
load-evidence and benchmark issue.

## 6. Known limitations, kept explicit

- API storage is process-local, not a persistent database or physical IoT stream.
- `datetime-local` controls have minute precision; sub-minute API range queries can be made directly, but the browser demos use exact minute boundaries.
- A sequence cursor orders equal-timestamp readings by original acceptance order; if a sensor receives additional packets between HTTP pages, the endpoint does not promise transaction-level snapshot isolation.
- The API does not provide a total-count field. The 10,002 count is established by the verification run's accepted receipts and explicit bounded traversal, not by a hidden frontend count.
- None of these tests is a substitute for controlled performance measurements or Docker validation in P3-11.

## 7. Completion checklist

- [ ] Four new routine browser scenarios passed locally.
- [ ] Commit 1 and Commit 2 browser regressions remained green.
- [ ] Full Playwright suite passed (one intentionally skipped large-dataset case when the opt-in variable is absent).
- [ ] Opt-in 10,002-packet real-API verification passed and its attachment was inspected.
- [ ] Backend restore/build/test/format gates passed.
- [ ] Git whitespace check passed and GitHub Actions is green.
- [ ] Commit pushed; PR prepared only after all three commits are green.

**Next issue:** P3-10 proactive insights and Part 2 prediction feedback.
