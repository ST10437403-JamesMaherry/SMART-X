# Smart-X Part 2 evidence

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
