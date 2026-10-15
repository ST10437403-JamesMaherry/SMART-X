# Smart-X Part 2 evidence

This directory contains reproducible demonstrations of the actual Smart-X
backend data structures and their integration with the existing Part 1 system.

Each demonstration has:
- A PNG evidence report for the lecturer.
- A matching HTML rendering.
- Raw JSON captured from actual API responses.
- A Python reproduction script in `scripts/`.

The reports are generated from validated HTTP results. They are not screenshots
of unimplemented React features.

---

## 06 — Chronological operational history with SortedDictionary

### Evidence files

- `06-sorted-history-timeline.png`
- `06-sorted-history-timeline.html`
- `06-sorted-history-timeline.json`
- `scripts/capture_sorted_history.py`

### Implementation

The new operational history uses a real, per-device:

`SortedDictionary<DateTimeOffset, List<HistoryEntry>>`

The timestamp is the sorted dictionary key. The corresponding list preserves
multiple events sharing exactly the same timestamp.

Each event also receives a store-assigned sequence number to provide
deterministic ordering when timestamps are equal.

The store supports:
- Telemetry events recorded only after successful worker processing.
- Accepted manual commands, including audited no-ops.
- Genuine Undo reversals linked to their original commands.
- Native Float, Integer and Boolean telemetry payloads.
- Backfilled readings with earlier capture timestamps.
- Equal timestamps without event replacement.
- Idempotent callback handling using stable event identities.
- Device-specific chronological histories.
- Ascending and descending history queries.
- A default 100-event query limit and maximum 500-event query limit.
- An independent 10,000-event retention capacity per device.

Insertion into a timestamp bucket uses the SortedDictionary's balanced-tree
lookup, with O(log n) key lookup/insertion in the number of distinct timestamp
keys. Retrieval follows the actual ordered keys.

Retention and query limits belong to this operational-history projection.
They do not modify the Part 1 telemetry repository.

### Reproduce the evidence

Start a local Smart-X instance:

```bash
docker compose config --quiet
docker compose up -d --build
```

From the repository root:

```bash
python3 scripts/capture_sorted_history.py --base-url http://localhost:8080
```

The script registers a new Integer sensor and a new Actuator/Boolean sensor.

It tests three separate order guarantees using four actual Integer readings:

| Reading | Value | Capture timestamp | Ingestion position |
| --- | ---: | --- | ---: |
| A | 10 | Base + 10 seconds | 1 |
| B | 11 | Base + 30 seconds | 2 |
| C | 100 | Base + 30 seconds | 3 |
| D | 12 | Base | 4 |

C is a critical observation because it arrives after B with an equal capture
timestamp and a significant value change.

D is a backfilled observation arriving last but captured earliest.

The verified result is:

| System | Order |
| --- | --- |
| Part 1 original telemetry ingestion | A, B, C, D |
| P2-03 actual worker dispatch | C, A, B, D |
| P2-05 chronological SortedDictionary | D, A, C, B |

B and C share one UTC occurrence timestamp. They are both preserved in the
same timestamp bucket. C appears before B within that bucket because C
completed processing first.

The original Part 1 ingestion order remains unchanged.

The capture also verifies that the latest processed Integer observation
remains C = 100, despite subsequent processing of older ordinary packets.

### Mixed command and telemetry history

The script registers an Online Actuator/Boolean device and performs:

1. Accept and process one Boolean telemetry reading of false.
2. Execute RelayOn: commanded state Off to On.
3. Execute RelayOn again: accepted no-op, with unchanged Undo depth.
4. Execute Undo: restore the original commanded Off state.
5. Repeat the stale Undo request: HTTP 409, without a new history entry.

The four successful events appear in one chronological timeline:
- TelemetryProcessed
- RelayCommand
- RelayCommand (no-op)
- RelayUndo

The Undo event retains its `reversesCommandId` reference.

Reported Boolean telemetry, operational device status, and commanded relay
state are verified as independent concepts.

### Render the screenshot

From `src/SmartX.Client`, use the existing Playwright package:

```bash
node --input-type=module <<'NODE'
import { chromium } from '@playwright/test';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = path.resolve(
  '../../docs/evidence/part2/06-sorted-history-timeline.html'
);

const output = path.resolve(
  '../../docs/evidence/part2/06-sorted-history-timeline.png'
);

const browser = await chromium.launch();

try {
  const page = await browser.newPage({
    viewport: { width: 1440, height: 1000 }
  });

  await page.goto(pathToFileURL(input).href);

  await page.screenshot({
    path: output,
    fullPage: true
  });
} finally {
  await browser.close();
}
NODE
```

### Automated test coverage

The P2-05 implementation is covered by:

- `tests/SmartX.Domain.Tests/Realtime/HistoryEntryTests.cs`
- `tests/SmartX.Infrastructure.Tests/Realtime/InMemoryRealtimeHistoryStoreTests.cs`
- `tests/SmartX.Application.Tests/Realtime/OperationalHistoryRecorderTests.cs`
- `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeHistoryEndpointTests.cs`
- `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeHistoryCompatibilityTests.cs`

The API compatibility suite verifies:
- Three 151-packet jagged batches: Float, Integer and Boolean.
- All actual processed packets appear in the independent history.
- The Part 1 recent telemetry endpoint retains its original ordering.
- The P2-03 processing diagnostic window remains capped at 100.
- Critical priority bypass and real chronological ordering.
- Out-of-order capture timestamps.
- Equal UTC instants expressed using different offsets.
- Telemetry, commands, no-ops, and Undo in the same history.
- Invalid and bounded history queries.
- Independent retention policies.
- Failed worker dispatches never appearing as successfully processed.

Following P2-05 Commit 3, the full backend regression was reported green
with **568 passing tests and 0 failures**.

### Limitations

Operational history is intentionally process-local and bounded.

Restarting the application clears the operational-history projection.
The system does not claim durable audit storage, database event sourcing, or
replay after an application restart.

The integrated React history UI is planned for P2-07.

---

## 04 — Real actuator commands and genuine Stack Undo

### Evidence files

- `04-command-history-undo.png`
- `04-command-history-undo.json`
- `04-command-history-undo.html`
- `scripts/capture_command_history_undo.py`

`04-command-history-undo.png` is an evidence report rendered from a verified
local HTTP run, not a screenshot of the future P2-07 React command dashboard.

Its companion JSON stores the actual registration, commanded-state snapshots,
command results, permanent audit, and rejected stale Undo.

### Reproduce on your Mac

Start Smart-X using the existing Docker configuration:

```bash
docker compose config --quiet
docker compose up -d --build
```

From the repository root:

```bash
python3 scripts/capture_command_history_undo.py \
  --base-url http://localhost:8080
```

The script creates a fresh Actuator/Boolean device, transitions it Online,
executes RelayOn, accepts a no-op, undoes the original command, and verifies
that a repeated stale Undo is rejected.

The script validates actual state and audit results before writing evidence.

### What this evidence proves

| Observation | Meaning |
| --- | --- |
| Initial Off; Undo depth 0 | Explicit initial commanded state. |
| RelayOn: Off to On; Undo depth 1 | A successful state change enters the real device Stack. |
| Repeated RelayOn: no-op; depth still 1 | No-ops are audited but are not undoable. |
| Undo: On to Off; depth 0 | Actual previous state is restored and Stack entry popped. |
| Original, no-op, reversal retained | Undo preserves command provenance. |
| Repeated stale Undo returns HTTP 409 | Stale Undo cannot mutate state or history. |
| Sensor Online; reported telemetry unchanged | Commanded state is separate from sensor status and observations. |

### Automated coverage and code locations

- `src/SmartX.Infrastructure/Commands/InMemoryActuatorCommandStore.cs`
- `src/SmartX.Application/Commands/ActuatorCommandService.cs`
- `src/SmartX.Api/Controllers/RealtimeCommandsController.cs`
- `tests/SmartX.Application.Tests/Commands/ActuatorCommandServiceTests.cs`
- `tests/SmartX.Infrastructure.Tests/Commands/ActuatorCommandUndoIntegrationTests.cs`
- `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeCommandsEndpointTests.cs`

Commands and audit are process-local. Restarting the API clears registered
devices and command history.

The interactive command dashboard is planned for P2-07.

---

## 03 — FIFO processing and critical bypass

### Evidence files

- `03-priority-alert-bypass.png`
- `03-priority-alert-bypass.json`
- `03-priority-alert-bypass.html`
- `scripts/capture_priority_bypass.py`

`03-priority-alert-bypass.png` presents a recorded API demonstration of
actual dispatch order. Its JSON retains the authoritative API snapshots.

The report preserves server dequeue order and does not infer processing
order from dashboard sorting.

### Reproduce on your Mac

Start a quiet local instance:

```bash
docker compose up -d --build
```

From the repository root:

```bash
python3 scripts/capture_priority_bypass.py \
  --base-url http://localhost:8080
```

Open the generated HTML and capture it as:

`docs/evidence/part2/03-priority-alert-bypass.png`

The capture creates one Integer sensor and submits A = 10, B = 11, and
C = 100.

It pauses ordinary dispatch before A and B enter the FIFO queue, verifies
that they are pending, and then accepts critical C.

C completes through the priority lane while A and B remain pending.
After ordinary dispatch resumes, the worker processes A followed by B.

The script restores the previous ordinary-dispatch pause setting,
including when evidence capture fails.

### What this evidence proves

| Observation | Meaning |
| --- | --- |
| A/B pending before C | Ordinary work waits in the real FIFO queue. |
| C dispatches first | A later critical packet bypasses pending ordinary work. |
| Processing order C, A, B | Critical-first priority, FIFO within the ordinary lane. |
| Three accepted and three processed | All work is accounted for. |
| Latest observation remains C = 100 | Older queued work cannot replace newer state. |
| Part 1 recent telemetry remains A, B, C | Existing ingestion order is preserved. |

### Automated coverage and code locations

- `tests/SmartX.Api.IntegrationTests/Realtime/TelemetryPriorityCompatibilityTests.cs`
- `tests/SmartX.Api.IntegrationTests/Realtime/TelemetryProcessingScenarioFactory.cs`
- `src/SmartX.Infrastructure/Realtime/InMemoryTelemetryWorkBuffer.cs`
- `src/SmartX.Domain/Realtime/TelemetryWorkPriority.cs`
- `src/SmartX.Api/Realtime/TelemetryProcessingWorker.cs`
- `src/SmartX.Infrastructure/Realtime/InMemoryTelemetryProcessingStore.cs`

The 151-packet tests inspect the actual independent completion log,
not just the bounded public diagnostic window.

Processing storage is process-local. These tests verify retained and
accounted work while the application is running, not durable replay
after process exit.
