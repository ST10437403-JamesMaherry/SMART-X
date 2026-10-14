# Smart-X Part 2 evidence

## 04 — Real actuator commands and genuine Stack Undo

`04-command-history-undo.png` is an evidence report rendered from a verified local HTTP run, not a screenshot of the future P2-07 React command dashboard. Its companion JSON stores the actual registration, commanded-state snapshots, command results, permanent audit, and rejected stale Undo. The HTML is a readable presentation of the same verified responses.

### Reproduce on your Mac

Start the current P2-04 branch through Docker (using the existing configured encryption settings):

```bash
docker compose config --quiet
docker compose up -d --build
```

From the repository root, create a fresh Actuator/Boolean device, transition it Online, execute RelayOn, accept a no-op, undo the original command, and verify a second stale Undo is rejected:

```bash
python3 scripts/capture_command_history_undo.py --base-url http://localhost:8080
```

The script verifies actual state and audit data after each transition before writing the JSON and HTML. It uses a unique identifier, leaves the demo device registered until the process restarts, and does not change existing devices or telemetry. An alternate trusted local development API is supported with `--base-url https://localhost:7095`.

Render the generated HTML to a PNG with the existing Playwright dependency, from `src/SmartX.Client`:

```bash
node --input-type=module <<'NODE'
import { chromium } from '@playwright/test';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const html = path.resolve('../../docs/evidence/part2/04-command-history-undo.html');
const png = path.resolve('../../docs/evidence/part2/04-command-history-undo.png');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 940 } });
  await page.goto(pathToFileURL(html).href);
  await page.screenshot({ path: png, fullPage: true });
} finally {
  await browser.close();
}
NODE
```

If the configured browser is unavailable, run `npx playwright install chromium`, or open the HTML in a browser and capture it manually with macOS Shift–Command–4.

### What this evidence proves

| Observation | Meaning |
| --- | --- |
| A newly registered Online Actuator/Boolean device begins commanded Off | Initial commanded relay state is explicit and separate from operational status/reported readings. |
| RelayOn returns Off → On and Undo depth 1 | A successful manual state change enters the real per-device Stack. |
| A second RelayOn is recorded as a no-op with Undo depth still 1 | No-op commands are auditable but never create new Undo entries. |
| Undo returns On → Off, depth 0 and `reversesCommandId` matching original | Undo restores the recorded prior state and pops the actual latest Stack entry. |
| Audit still contains original, no-op and reversal | Undo does not erase command provenance or cause a reversible toggle loop. |
| Reusing the original command ID for another Undo returns ProblemDetails HTTP 409 | Stale Undo is rejected without modifying commanded state, audit, or Stack. |
| Sensor remains Online and no telemetry is fabricated | Commands do not rewrite operational sensor status or reported readings. |

### Automated coverage and code locations

- `src/SmartX.Infrastructure/Commands/InMemoryActuatorCommandStore.cs`: per-device serialisation and actual `Stack<CommandRecord>` history.
- `src/SmartX.Application/Commands/ActuatorCommandService.cs`: current authoritative eligibility validation, expected-latest Undo and genuine prior-state restoration.
- `src/SmartX.Api/Controllers/RealtimeCommandsController.cs`: normal HTTP command, Undo, and command-history endpoints.
- `tests/SmartX.Application.Tests/Commands/ActuatorCommandServiceTests.cs`: request validation, eligibility, no-ops, stale Undo and failed reversal safeguards.
- `tests/SmartX.Infrastructure.Tests/Commands/ActuatorCommandUndoIntegrationTests.cs`: real repository plus real Stack LIFO, isolation and concurrent Undo behaviour.
- `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeCommandsEndpointTests.cs`: actual HTTP outcomes, ProblemDetails, state snapshots, audit linkage and notification failure compatibility.

Commands and audit are process-local. Restarting the API clears registered devices and command history; this is not durable device-control storage. The command dashboard UI is planned for P2-07.

---

## 03 — FIFO processing and critical bypass

`03-priority-alert-bypass.png` shows a recorded API demonstration of actual dispatch order. Its adjacent JSON retains the authoritative API snapshots; its HTML presents those records for review. The report preserves server dequeue order. It does not infer processing order from dashboard sorting.

### Reproduce on your Mac

Start a quiet local instance from the current branch:

```bash
docker compose up -d --build
```

From the repository root, record a fresh demonstration:

```bash
python3 scripts/capture_priority_bypass.py --base-url http://localhost:8080
```

Open the generated HTML in your browser and capture it using macOS **Shift–Command–4**, saving the screenshot as `docs/evidence/part2/03-priority-alert-bypass.png`. The supplied PNG is an export of the recorded API dispatch data; generating your own browser screenshot demonstrates your local run.

The capture creates one fresh Integer sensor and submits A = 10, B = 11, then C = 100. It requires no existing pending or executing work, pauses ordinary dispatch, confirms A/B have not dispatched, waits for C to complete while A/B remain pending, resumes ordinary work, and verifies C/A/B dispatch order. It checks all three packets are accounted for, the latest device observation remains C, and Part 1 recent history remains A/B/C. It restores the original pause setting even when capture fails. The demo sensor remains registered for inspection until the process restarts.

The script defaults to Docker's port 8080. A trusted local development endpoint can also be used:

```bash
python3 scripts/capture_priority_bypass.py --base-url https://localhost:7095
```

### What this evidence proves

| Observation | Meaning |
| --- | --- |
| A/B pending before C; dispatched count unchanged | Ordinary work was genuinely waiting in the FIFO queue. |
| C's higher enqueue sequence with the first dequeue sequence | Later critical work bypassed pending ordinary work. |
| Dequeue order C, A, B | Critical-first choice at dispatch and FIFO within the ordinary lane. |
| Three accepted and three processed demo packets | Every accepted packet was accounted for. |
| Latest device observation stays at C = 100 | Older queued work cannot replace a newer observation. |
| Recent-history endpoint still returns A/B/C | Part 1 persistence order is preserved. |

The checked-in capture records a local development API run. Regenerate it on your own Mac for your video/demo; sequences can start above one when the process has already handled other work. The report prints the actual sequence values.

### Automated coverage and code locations

- `tests/SmartX.Api.IntegrationTests/Realtime/TelemetryPriorityCompatibilityTests.cs`: ordinary FIFO, pending A/B versus later C, equal-timestamp observations, stable equal critical priorities, non-preemption, 48 concurrent requests, 151-packet jagged batches for all types, anomaly recovery, visible worker failure and host shutdown.
- `tests/SmartX.Api.IntegrationTests/Realtime/TelemetryProcessingScenarioFactory.cs`: a scoped decorator around the actual processor records all completions; an optional gate controls scheduling only inside tests. Production worker/buffer/store/persistence are used.
- `src/SmartX.Infrastructure/Realtime/InMemoryTelemetryWorkBuffer.cs`: actual `Queue<T>` and `PriorityQueue<TElement, TPriority>` admission and critical-first dispatch.
- `src/SmartX.Domain/Realtime/TelemetryWorkPriority.cs`: explicit severity comparer and arrival-order tie breaker.
- `src/SmartX.Api/Realtime/TelemetryProcessingWorker.cs`: the single hosted consumer and operation scopes.
- `src/SmartX.Infrastructure/Realtime/InMemoryTelemetryProcessingStore.cs`: observation recency, completion counts, retained failures and bounded diagnostic dispatch records.

The 151-packet tests inspect an independent completion log for all 151 exact packets. Both recent telemetry and public diagnostic dispatch history remain capped at 100; lifetime processing counts cover the entire batch.

Run the focused processing suite and then the backend gates:

```bash
dotnet test SmartX.sln --configuration Release --filter 'FullyQualifiedName~Realtime|FullyQualifiedName~Telemetry'
dotnet restore SmartX.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
dotnet format SmartX.sln --no-restore --verify-no-changes
dotnet build SmartX.sln --configuration Release --no-restore
dotnet test SmartX.sln --configuration Release --no-build
```

The failure test injects an exception after a dispatch begins and checks the live diagnostics API remains available with `Faulted`, the exact failed packet retained, pending work untouched, and later accepted work still accounted for. The shutdown test stops the real test host with a dispatch held and both lanes pending; it checks `Interrupted` retained work and balanced counts after stopping.

Processing storage is process-local. These tests prove retained/accounted work while the process exists; they do not claim durable storage or replay after process exit. The Part 2 React dashboard remains planned for P2-07.
