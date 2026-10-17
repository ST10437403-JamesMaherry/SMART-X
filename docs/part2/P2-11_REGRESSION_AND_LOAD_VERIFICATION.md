# P2-11 / Commit 1 — Regression and finite-load verification

## Purpose

This document supplies the reproducible checks for the final Smart-X Part 2
release review. It does **not** claim that the checks have passed. Run them on
the real repository and retain the actual output before recording results.

P2-11 is a release and evidence issue, not a redesign of the completed Part 1
or Part 2 implementation. Preserve existing API contracts, security checks,
worker semantics and visual workflows.

## Branch and quality-gate setup

From the repository root, after confirming that P2-10 was merged into `dev`:

```bash
git status --short --branch
git switch -c chore/part2-final-delivery

dotnet restore SmartX.sln \
  -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low

dotnet format SmartX.sln --no-restore --verify-no-changes
dotnet build SmartX.sln --configuration Release --no-restore
dotnet test SmartX.sln --configuration Release --no-build
```

From `src/SmartX.Client`, with the dependency lockfile present:

```bash
npm ci
npm audit --audit-level=high
npm run lint
npm run format:check
npm run build
npx playwright test e2e/part2-final-load-regression.spec.ts
CI=true npm run test:e2e
```

The added E2E test is one normal CI test, **not a skipped or mocked scenario**.
It uses the same isolated ASP.NET Core server started by Playwright, creates
three real registered devices and submits 36 readings as jagged HTTP batches.

### Exact assertions added by the new test

1. The actual worker is healthy, unpaused and idle before the measured test.
2. Three unique sensors are registered: Float Environmental, Integer Power
   Consumption, and Boolean Actuator.
3. Each sensor accepts uneven jagged batches (7 + 5 packets); the API receipt
   confirms the accepted type, sensor, count and batch structure.
4. All 36 accepted readings become *completed* worker events. The worker's
   accepted and processed count deltas agree, with zero unfinished work.
5. Part 1 telemetry is preserved in original ingestion order, with true and
   false values retained correctly.
6. The separate P2-05 operational timeline contains every processed reading.
   Two readings per sensor intentionally share one capture timestamp, and both
   must survive in ascending chronological order.
7. The active-condition snapshot contains no duplicate membership keys for
   the generated sensors. MissingConfiguration may legitimately be present
   because this focused test does not upload configuration files.

No fabricated counters, timing thresholds, test-only API bypasses or locally
invented worker histories are used.

## Existing Part 1 and Part 2 regression coverage

Run the **full solution**, not just the new test. The previously implemented
tests cover these requirements; inspect the named tests when gathering evidence:

| Behaviour | Existing test/source area |
|---|---|
| Domain validation, registration and canonical identifiers | `tests/SmartX.Domain.Tests/Sensors/`, `tests/SmartX.Api.IntegrationTests/Sensors/` |
| Float / Integer / Boolean packets and generic deltas | `tests/SmartX.Domain.Tests/Telemetry/`, `tests/SmartX.Api.IntegrationTests/Telemetry/` |
| Jagged batches and Part 1 order | `tests/SmartX.Domain.Tests/Telemetry/HistoricalTelemetryBufferTests.cs`, `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeHistoryCompatibilityTests.cs` |
| Recursive deployment tree | `tests/SmartX.Domain.Tests/Deployment/DeploymentTreeValidatorTests.cs` |
| Spotlight issue acknowledgement, recovery and recurrence | `tests/SmartX.Application.Tests/Dashboard/`, `tests/SmartX.Api.IntegrationTests/Dashboard/` |
| Encrypted attachments, validation, download, tamper rejection | `tests/SmartX.Api.IntegrationTests/Attachments/`, `tests/SmartX.Infrastructure.Tests/Attachments/` |
| CORS and ProblemDetails | `tests/SmartX.Api.IntegrationTests/Security/CorsPolicyTests.cs` and API integration tests |
| Real Queue / PriorityQueue, FIFO and critical bypass | `tests/SmartX.Infrastructure.Tests/Realtime/TelemetryWorkBufferTests.cs`, `tests/SmartX.Api.IntegrationTests/Realtime/TelemetryPriorityCompatibilityTests.cs` |
| Real Stack-backed Undo | `tests/SmartX.Infrastructure.Tests/Commands/`, `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeCommandsEndpointTests.cs` |
| SortedDictionary history, equal-time preservation | `tests/SmartX.Infrastructure.Tests/Realtime/InMemoryRealtimeHistoryStoreTests.cs`, `tests/SmartX.Api.IntegrationTests/Realtime/RealtimeHistoryCompatibilityTests.cs` |
| HashSet unique active conditions | `tests/SmartX.Domain.Tests/Dashboard/DashboardActiveConditionSetTests.cs`, `tests/SmartX.Api.IntegrationTests/Dashboard/RealtimeActiveConditionRegressionTests.cs` |
| Canonical search, deliberate history, prediction and suggestion UI | P2-08, P2-09 and P2-10 tests on the merged `dev` branch |

P2-10's existing full E2E baseline was **54 tests** at its final green run.
With one new test, expect **55 total** if no other spec changes are merged.
Do not copy an earlier backend count into the final report: use the actual
current `dotnet test SmartX.sln` summary.

## Reproducible finite load against the running Docker application

Run the following on a quiet **disposable local development instance**. The
worker, devices and history are process-local; the verifier first ensures the
worker has no outstanding work, then records process-wide counter deltas.
Do not run another simulator or telemetry test concurrently.

```bash
# From the repository root:
docker compose config --quiet
docker compose up -d --build

docker compose ps
curl -fsS http://localhost:8080/api/health

# Small smoke check (3 devices x 12 readings; writes no files):
python3 scripts/verify_part2_finite_load.py \
  --base-url http://localhost:8080 \
  --devices 3 \
  --readings-per-device 12
```

Once the smoke check has passed, execute the **100 x 100** finite scenario:

```bash
python3 scripts/verify_part2_finite_load.py \
  --base-url http://localhost:8080 \
  --devices 100 \
  --readings-per-device 100 \
  --timeout-seconds 300 \
  --output docs/evidence/part2/part2-load-summary.json
```

The verifier sends real registered-device requests and typed jagged batches
with a maximum inner batch size of 25. For each run it verifies:

- The exact submitted and server-accepted reading counts.
- Actual worker-processed count delta, pending count and failure status.
- **Every device's independent processed history** (not just the most recent
  100 global dispatch diagnostics).
- All Part 1 original ingestion-order values.
- UTC equal-timestamp event retention.
- Unique HashSet membership keys among the run's active conditions.
- Environment and observed elapsed time, clearly labelled *non-benchmark*.

It refuses to save `part2-load-summary.json` after a failed assertion. The
summary records actual device IDs, per-type counts, accepted batches and
worker snapshots from that specific run. It is not a synthetic fixture.

Review the generated report before committing it. Commit the actual result
and final evidence in **P2-11 Commit 3**, not in this test-harness commit.

### Safety and scope

- Limit: 100 devices, 100 readings/device and 10,000 total accepted readings.
- The script does not change security policies, retry writes, bypass validation
  or pretend accepted work has already been processed.
- It creates process-local simulation devices. Run on a disposable instance,
  not on a shared application containing real operator data.
- `docker compose down` performs normal shutdown. **Do not use `-v`**: the
  existing encrypted attachment volume must not be deleted.
- Treat run time as a local observation. No throughput or latency SLA is
  inferred from one run.

## Recording actual results

Before creating this commit, record the actual commands and outputs in your
local review notes. For the Part 2 evidence submission later, capture:

- Actual backend passed / failed test counts and .NET SDK version.
- Actual frontend quality-gate and Playwright passed / failed counts.
- The verifier-produced JSON and its device/readings configuration.
- Container health, API privacy and route checks in P2-11 Commit 2.
- A CI run with all four required checks green.

Do not invent screenshots, local timings, marks, lecturer feedback, or CI
results. Final README/rubric documentation and the video outline belong to
P2-11 Commit 3; release preparation belongs to Commit 4.
