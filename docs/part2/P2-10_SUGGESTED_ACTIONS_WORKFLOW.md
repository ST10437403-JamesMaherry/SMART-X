# P2-10 — Suggested Actions: verified operator workflow

**Assessment area:** Predictive Action and Suggestion Engine (30 marks), integrated
navigation (30 marks), and genuine Stack-backed command Undo.

**Implementation:** `feature/part2-suggested-actions` (P2-10, Commit 3)

## What the lecturer should see

Smart-X does not offer unexplained, hard-coded suggestions. The existing P2-09
engine observes **real recorded operator activity** in the same ASP.NET Core
process as the command and alert services. The Operations Console presents the
result, not a fabricated training counter or an LLM-generated decision.

One eligible training episode requires, in order:

1. An **unresolved active source alert** from the genuine HashSet-backed issue collection.
2. An explicit **View active alert** operation, recorded as `AlertViewed` with that episode ID.
3. A subsequent **Associate with source alert** action on the searched actuator, recorded as `SearchSubmitted` with that same episode ID and selected target ID.
4. A state-changing **RelayOn** command accepted through the **normal command endpoint**. Its server-recorded `CommandExecuted` event is the only successful-response training evidence.

The learner deduplicates events by alert episode, retains evidence for seven days,
and requires **three independent matching episodes**. Passive GETs, typing,
refresh, no-op commands, failed commands, and Undo do not create new training
support. The data remains process-local and resets on API restart.

## Reproducible moisture example

The automated test uses a uniquely registered **Environmental / Float** source
labelled as a *simulated moisture sensor*, and an **Online Actuator / Boolean**
relay. Moisture is only the fixture label: the project currently has **no
absolute 20% moisture limit**.

The existing adaptive anomaly classifier instead evaluates the relative
change between consecutive readings captured within **five minutes**:

| Step | Value | Captured interval | Actual server result |
| --- | ---: | --- | --- |
| Starting baseline | 50 | First reading | No spike (no previous value) |
| Moisture decrease | 10 | 1 minute after 50 | 80% relative **decrease**, Critical `TelemetrySpike` |
| Recovery reading 1 | 10 | 30 seconds later | First stable comparison; issue remains |
| Recovery reading 2 | 10 | 30 seconds later | Second stable comparison; issue resolves |
| Restore baseline | 50 | More than 5 minutes later | Stable under the *existing maximum-spike-interval* rule |

The sequence is repeated **three times** to create **three distinct alert
IDs**, each accompanied by a real user search, alert view, actuator association,
accepted `RelayOn`, and real Stack-backed Undo. Undo restores the initial Off
state so the next episode can produce a state-changing command.

Captured timestamps are simulated **historical** UTC instants, and advance
monotonically in the test; the runner does not have to sleep for the full
captured-time intervals. Evidence and condition freshness use the backend's real
`TimeProvider` and accepted HTTP operation timestamps. All telemetry is
persisted through the existing typed telemetry endpoint; the test never inserts
issue records or training-pattern entries directly.

On the **fourth** matching 50 -> 10 decrease, the test deliberately performs
**no new search, alert view, or association**. The backend must already have
three supporting episodes and return a live, explained `RelayOn` recommendation.
The visible UI must show:

- The actual source and actuator names, triggering condition and movement.
- `3 distinct episodes` and the bounded heuristic band/score.
- The server-created explanation and evaluation expiry.
- A **Review Relay On** button rather than automatic execution.

The operator can use the keyboard to review and cancel without any POST. On
confirmation, Smart-X re-reads recommendation eligibility and issues a single
`POST /api/realtime/devices/{targetId}/commands` with `RelayOn`. The same
real command history and Undo Stack update as a manual command. Selecting
**Undo latest command** must restore Off and return Undo depth to zero.

The test also verifies cross-pillar retained selection and search fields, a
375 px mobile layout without horizontal document overflow, and a real SignalR
initial outage/reconnect followed by an authoritative recommendation GET.
Previous P2-10 tests cover rejected/offline targets, withdrawn recommendations,
failed revalidation, expiry, and read-only refresh behaviour.

## Automated reproduction

Run from the repository root on a clean checkout of the feature branch:

```bash
cd src/SmartX.Client
npm ci
npx playwright test e2e/part2-suggested-actions-journey.spec.ts
```

`playwright.config.ts` starts the **real** .NET API on `127.0.0.1:5095`
and the React/Vite app on `localhost:5173` using isolated process-local stores.
Do not start another server on those ports while running Playwright. A new test
process starts with a fresh API session; production credentials are not used.

For the complete regression:

```bash
npm run lint
npm run format:check
npm run build
npm run test:e2e
cd ../..
dotnet test SmartX.sln --configuration Release
```

## Capture actual browser evidence

Ordinary Playwright runs do **not** write evidence files. To capture them,
run this from `src/SmartX.Client` after confirming the focused journey passes:

```bash
SMARTX_CAPTURE_P210_EVIDENCE=1 npx playwright test \
  e2e/part2-suggested-actions-journey.spec.ts \
  --grep "three real moisture-drop episodes"
```

This captures **actual real-API/browser output**, never rendered mocks:

| Screenshot | What it proves |
| --- | --- |
| `docs/evidence/part2/08-suggested-actions.png` | Real learned recommendation, three episodes, source condition, confidence band and explanation before the fourth alert is searched |
| `docs/evidence/part2/09-suggested-action-undo.png` | Server-confirmed Off relay after Undo, restored Stack depth, and accepted audit |

Capture is guarded by the environment variable so normal CI regressions do not
create untracked PNGs. If you generated the evidence, inspect both images and
stage them **along with this commit**. Do not claim screenshots were generated
until the capture command has actually run successfully.

## Code and contracts to show during marking

| Area | Actual implementation |
| --- | --- |
| Active conditions (HashSet) | `src/SmartX.Domain/Dashboard/DashboardIssueCollection.cs` |
| Deliberate interaction history | `src/SmartX.Application/Interactions/OperatorInteractionService.cs` |
| Genuine learning evidence | `src/SmartX.Application/Recommendations/PredictivePatternLearningService.cs` |
| Bounded Dictionary patterns | `src/SmartX.Infrastructure/Recommendations/InMemoryInteractionPatternStore.cs` |
| Ranked, explainable suggestions | `src/SmartX.Application/Recommendations/PredictiveActionService.cs` |
| Live predictions HTTP API | `src/SmartX.Api/Controllers/RealtimeRecommendationsController.cs` |
| Operator-selected command/Undo | `src/SmartX.Api/Controllers/RealtimeCommandsController.cs` |
| React interface and confirmation | `src/SmartX.Client/src/features/realtime/SuggestedActions.tsx` |
| Real browser proof | `src/SmartX.Client/e2e/part2-suggested-actions-journey.spec.ts` |

**Interpretation:** The `85/100 High` score is a deterministic support/recency/
severity **heuristic**, not a calibrated 85% probability. Suggestions are
advisory, time-bounded, and revalidated before execution. All current
simulation data, alert episodes, learning evidence, and command Stacks are
process-local; no durable model training or autonomous actuation is claimed.
