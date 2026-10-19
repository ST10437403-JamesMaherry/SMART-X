# Smart-X Part 2 — High-Band Rubric and Verification Matrix

**Source:** `PROG7312POE.pdf`, Part 2 technical requirements (pages 7–9) and Part 2 marking grid (pages 22–23). **Total: 100 marks.** This matrix points to implemented behaviour and verification; it does **not** promise the marker's awarded grade.

[Project README](../../README.md) · [Screenshot index](../evidence/part2/README.md)

## 1. Main Menu Navigation and Active Pillar State — 30 marks

**High-band intent:** asynchronous movement between active Part 1 and Part 2 views without losing useful application state, freezing the interface or breaking existing Part 1 features.

**Implemented:** React Router exposes the `/` gateway, `/ingestion` for Part 1 and `/realtime` for Part 2; `WorkspaceStateProvider` preserves selected device and safe search/filter drafts between pillars. Topology remains an explicitly unavailable third pillar. SignalR triggers authoritative HTTP refreshes; normal navigation does not replace the sensor registry or inject fake interactions. Responsive layouts and keyboard controls remain usable.

**Where to inspect:**

- `src/SmartX.Client/src/components/PillarNavigation.tsx`
- `src/SmartX.Client/src/state/WorkspaceStateProvider.tsx`
- `src/SmartX.Client/src/features/realtime/RealtimeWorkspace.tsx`
- `src/SmartX.Client/e2e/part2-navigation.spec.ts`
- `src/SmartX.Client/e2e/part2-realtime-responsive.spec.ts`

**Evidence:** [`01-part2-navigation.png`](../evidence/part2/01-part2-navigation.png), [`09-responsive-part2.png`](../evidence/part2/09-responsive-part2.png), [`09-responsive-part2-simulator.png`](../evidence/part2/09-responsive-part2-simulator.png).

**Verification:** select a device, set a search/filter draft, navigate to Part 1 and back; verify the selection and draft still exist and the dashboard refresh remains functional. Confirm both active pillars render directly by URL and topology cannot be activated.

## 2. Advanced Collections: Stacks, Queues and Priority Queues — 15 marks

**High-band intent:** reliable FIFO ingestion with immediate critical bypass and a functioning LIFO command Undo, all integrated with live data.

**Implemented:**

- Ordinary traffic is buffered in a real `Queue<TelemetryWorkItem>`.
- Critical traffic uses a `PriorityQueue<TelemetryWorkItem, TelemetryWorkPriority>`; the worker selects the critical lane first, while the ordinary queue remains FIFO.
- A lock protects small admission/dequeue transitions; asynchronous processing occurs outside the lock.
- Manual relay state changes enter a device-specific `Stack<T>` in the command store; Undo reverses the latest eligible action, while accepted no-ops do not push a false Undo item.
- Worker diagnostics distinguish HTTP acceptance, actual dequeue and completed processing.

**Where to inspect:**

- `src/SmartX.Infrastructure/Realtime/InMemoryTelemetryWorkBuffer.cs`
- `src/SmartX.Api/Realtime/TelemetryProcessingWorker.cs`
- `src/SmartX.Infrastructure/Commands/InMemoryActuatorCommandStore.cs`
- `src/SmartX.Application/Commands/ActuatorCommandService.cs`
- `tests/SmartX.Infrastructure.Tests/Realtime/TelemetryWorkBufferTests.cs`
- `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeCommandsEndpointTests.cs`
- `src/SmartX.Client/e2e/part2-realtime-simulator.spec.ts`

**Evidence:** [`03-priority-alert-bypass.png`](../evidence/part2/03-priority-alert-bypass.png), [`04-command-history-undo.png`](../evidence/part2/04-command-history-undo.png), [`02-live-command-stream.png`](../evidence/part2/02-live-command-stream.png).

**Verification:** run **Run critical bypass** with ordinary packets queued. Compare real dispatch order: critical before the waiting ordinary work, then ordinary FIFO after resume. For commands, issue Relay On, accept a duplicate no-op, then Undo; confirm Off and Stack depth zero while the audit still records actions.

## 3. Lookup Structures: Dictionaries and Key-Value Maps — 15 marks

**High-band intent:** efficient canonical device identification and chronological history, including correct handling of equal-time events.

**Implemented:**

- A `Dictionary<TKey,TValue>` index maps canonical physical identifiers to the existing sensor profiles. Exact lookup is average **O(1)**, not a linear string scan.
- Free-text, category, status and active-condition search deliberately filters the server-owned registry snapshot; these broader scans are **not** described as O(1).
- `SortedDictionary<DateTimeOffset, List<HistoryEntry>>` stores per-device operational history in capture-time order. The list at each timestamp retains events sharing the same instant; sequence information gives stable tie ordering. Timestamp insertion/lookup is **O(log n)** in the number of unique timestamp keys.

**Where to inspect:**

- `src/SmartX.Infrastructure/Sensors/InMemorySensorRepository.cs`
- `src/SmartX.Application/Realtime/RealtimeDeviceSearchService.cs`
- `src/SmartX.Infrastructure/Realtime/InMemoryRealtimeHistoryStore.cs`
- `tests/SmartX.Infrastructure.Tests/Sensors/InMemorySensorRepositoryTests.cs`
- `tests/SmartX.Infrastructure.Tests/Realtime/RealtimeDeviceSearchLookupTests.cs`
- `tests/SmartX.Infrastructure.Tests/Realtime/InMemoryRealtimeHistoryStoreTests.cs`
- `src/SmartX.Client/e2e/part2-device-search.spec.ts`

**Evidence:** [`05-search-and-filter.png`](../evidence/part2/05-search-and-filter.png), [`05-search-empty-results.png`](../evidence/part2/05-search-empty-results.png), [`06-sorted-history-timeline.png`](../evidence/part2/06-sorted-history-timeline.png).

**Verification:** use equivalent canonical MAC formats in exact lookup; combine filters and confirm zero results are handled. For history, submit out-of-order captured timestamps and two equal instants; both must appear in deterministic chronological order, without modifying Part 1's original ingestion sequence.

## 4. Unique Collections: Sets — 10 marks

**High-band intent:** prevent repeated alerts or disconnects from creating duplicate unresolved memberships, with real recovery and recurrence.

**Implemented:** `DashboardIssueCollection` maintains active memberships with `HashSet<T>` while retaining issue metadata and occurrence counts. Repeated observations of one condition update its existing issue. Two stable readings can resolve a telemetry anomaly; restoring a disconnected sensor's status removes the unresolved disconnect membership. A later recurrence is a new episode.

**Where to inspect:**

- `src/SmartX.Domain/Dashboard/DashboardIssueCollection.cs`
- `src/SmartX.Application/Dashboard/DashboardIssueService.cs`
- `tests/SmartX.Domain.Tests/Dashboard/DashboardActiveConditionSetTests.cs`
- `tests/SmartX.Domain.Tests/Dashboard/DashboardActiveConditionConcurrencyRegressionTests.cs`
- `src/SmartX.Client/e2e/part2-realtime-simulator.spec.ts`

**Evidence:** [`07-unique-active-errors.png`](../evidence/part2/07-unique-active-errors.png), [`02-active-conditions.png`](../evidence/part2/02-active-conditions.png).

**Verification:** run **Run numeric spike** and **Run disconnect / recover**; compare the same membership key across repeated observations and confirm it is removed on recovery. Run recurrence to show a new genuine episode, without duplicate active keys.

## 5. Predictive Action and Suggestion Engine — 30 marks

**High-band intent:** proactive, relevant guidance based on real history, presented clearly and connected to the operating workflow.

**Implemented:**

1. The backend records deliberate alert views and source-associated actuator searches. Passive HTTP GETs, mount refreshes and pagination are not training events.
2. Only an eligible **successful state-changing** relay command linked to the same alert episode can train a pattern. Repeated clicks within one episode, no-ops, rejected commands and Undo do not constitute three independent successes.
3. A dictionary-backed process-local pattern store retains time-bounded evidence. The default policy requires **three independent episodes within seven days**.
4. The suggestion service compares that history to the **current** unresolved source condition, numeric movement and severity, plus the **current** target type, online status and commanded relay state. Recovered, stale, offline, already-satisfied or otherwise invalid targets are suppressed.
5. The panel explains the source, target, command, support count, heuristic confidence and expiry. **Review → Confirm** fetches fresh eligibility and uses the normal command endpoint; the operator can then Undo the real command.

**Where to inspect:**

- `src/SmartX.Application/Recommendations/PredictivePatternLearningService.cs`
- `src/SmartX.Infrastructure/Recommendations/InMemoryInteractionPatternStore.cs`
- `src/SmartX.Application/Recommendations/PredictiveActionService.cs`
- `src/SmartX.Api/Controllers/RealtimeRecommendationsController.cs`
- `src/SmartX.Client/src/features/realtime/SuggestedActions.tsx`
- `tests/SmartX.Api.IntegrationTests/Recommendations/PredictivePatternLearningEndpointTests.cs`
- `tests/SmartX.Api.IntegrationTests/Recommendations/PredictiveRecommendationsEndpointTests.cs`
- `src/SmartX.Client/e2e/part2-suggested-actions-journey.spec.ts`

**Evidence:** [`08-suggested-actions.png`](../evidence/part2/08-suggested-actions.png), [`05-search-alert-association.png`](../evidence/part2/05-search-alert-association.png), [`05-search-command-evidence.png`](../evidence/part2/05-search-command-evidence.png).

**Verification:** run the Playwright genuine learn/suggest/execute/Undo journey. It trains three distinct moisture-drop episodes, then verifies that a fourth matching drop produces a recommendation **before another search**. Review, Cancel, Confirm, normal API execution and Stack Undo must all behave correctly. The score is an explained **heuristic**, not an ML-derived probability; nothing auto-executes.

## Cross-cutting delivery evidence

| Area | Validation and primary file |
| --- | --- |
| Backend and Part 1 regressions | `dotnet test SmartX.sln --configuration Release --no-build`; includes telemetry, generics, recursion, anomalies, encrypted attachments, CORS and safe API errors. |
| Frontend and accessibility | `npm run lint`, `npm run format:check`, `npm run build`, `CI=true npm run test:e2e -- --retries=0`. |
| 100 × 100 finite processing | [`part2-load-summary.json`](../evidence/part2/part2-load-summary.json); `scripts/verify_part2_finite_load.py`; checks real acceptances, worker completions and histories. |
| Production deployment | `.github/workflows/ci.yml` and `scripts/verify_part2_docker_boundaries.py`; validates private API, non-root containers, direct route and both SignalR hubs. |
| GitHub delivery | Commit history, relevant PRs, four CI jobs, Markdown README, evidence and demonstration video/presentation once supplied. |

**Important:** Part 1 screenshot evidence is preserved in [`docs/evidence/part1`](../evidence/part1/README.md). The final `dev` → `main` merge, tag and release package are not claimed here; they belong to the P2-11 release-preparation commit and subsequent explicit release steps.
