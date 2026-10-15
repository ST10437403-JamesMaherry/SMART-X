# P2-06 — Unique active errors and recovery with HashSet

**PROG7312 Part 2 | Unique Collections: Sets (10 marks)**

Smart-X uses a real `HashSet<string>` as the authoritative membership index for unresolved sensor conditions. Each key combines the registered sensor's canonical GUID and the issue type. A separate dictionary stores the active issue's details, while existing bounded issue history keeps resolved episodes for audit. Membership and issue updates share the collection's existing synchronisation lock.

## Evidence artifacts

| File | Purpose |
| --- | --- |
| `07-unique-active-errors.png` | Single-page, high-contrast evidence screenshot for the lecturer. |
| `07-unique-active-errors.html` | Human-readable report rendered from captured, validated API responses. |
| `07-unique-active-errors.json` | Underlying real HTTP results for each of the 16 lifecycle stages (filtered to the two demonstration sensors). |
| `scripts/capture_unique_active_errors.py` | Reproducible standard-library-only capture and validation script. |

**This is an API evidence report, not a screenshot of a completed P2-07 React view.** The capture refuses to create output files if any invariant fails; it does not fabricate alert history or write directly to the collection.

## How to reproduce

Use the `feature/part2-active-error-set` branch (or the merged `dev` branch) and start the actual application from the repository root:

```bash
docker compose config --quiet
docker compose up -d --build
curl --fail --silent --show-error http://localhost:8080/api/health
```

The frontend/Nginx reverse proxy exposes the API at port `8080` by default. If `SMARTX_HTTP_PORT` is set to another port, use that port in both commands below.

```bash
python3 -m py_compile scripts/capture_unique_active_errors.py
python3 scripts/capture_unique_active_errors.py \
    --base-url http://localhost:8080
```

The script requires a healthy, unpaused telemetry worker and no outstanding queued work at the beginning of the run. It creates two new uniquely named test sensors, and never modifies existing sensor profiles or globally pauses the processing queue. Their data is process-local; an encrypted Configuration attachment is stored using the original Part 1 upload API. No credentials or encryption keys are written into the script or evidence.

To render the verified HTML report to PNG, from `src/SmartX.Client` (where `@playwright/test` is already a development dependency):

```bash
node --input-type=module <<'NODE'
import { chromium } from '@playwright/test';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const htmlFile = path.resolve('../../docs/evidence/part2/07-unique-active-errors.html');
const pngFile = path.resolve('../../docs/evidence/part2/07-unique-active-errors.png');
const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({
    viewport: { width: 1440, height: 1000 },
    deviceScaleFactor: 1,
  });
  await page.goto(pathToFileURL(htmlFile).href);
  await page.screenshot({ path: pngFile, fullPage: true });
  console.log(`Created ${pngFile}`);
} finally {
  await browser.close();
}
NODE
```

Run `npx playwright install chromium` in `src/SmartX.Client` first only if the browser is missing. Open the PNG to verify that the whole report is legible before committing it.

## Verified scenarios

1. **Idempotent configuration detection:** a new sensor has exactly one `ConfigurationIssue`. Repeated reads from both dashboards retain the original issue ID and occurrence count, instead of creating another active issue or reprocessing the missing state.
2. **Acknowledgement is not recovery:** acknowledging the Configuration issue changes its state to `Acknowledged`, but HashSet membership and active count remain unchanged.
3. **Configuration recovery:** uploading a real Configuration attachment removes the active key while retaining the resolved episode in Part 1 history.
4. **Warning, escalation, repeated-failure:** Float readings `100 → 140 → 300 → 700` produce three anomaly observations. The first creates a Warning `TelemetrySpike`. The second escalates the *same* issue to Critical. The third increments its occurrence count to three and creates a separate `RepeatedFailure` condition. The original spike remains one active membership key, even after acknowledgement.
5. **Two-stable-reading recovery:** subsequent values `701 → 702` confirm recovery. After the first stable reading, both conditions remain active; after the second, both keys are removed while the original episodes remain in historical issues.
6. **Fresh recurrence:** a later `1500` reading creates a new `TelemetrySpike` episode with the original stable membership key but a different issue ID and an occurrence count reset to one.
7. **Independent sensor membership:** a second sensor transitions `Online → Offline → Online`. Its own `SensorDisconnected` episode can be acknowledged and resolved without changing the first sensor's recurrent spike.
8. **Part 1 and P2-05 compatibility:** the original ingestion-order API and new processed chronological-history API both retain the seven actual typed Float values. The original dashboard API and the new active-conditions endpoint return matching issue identities, severities, states and occurrence counts at each captured phase.

## Relevant implementation and tests

- `src/SmartX.Domain/Dashboard/DashboardIssueCollection.cs` — authoritative HashSet membership, synchronised with the existing dictionary and issue history.
- `src/SmartX.Api/Controllers/DashboardIssuesController.cs` — Part 1 issue snapshots and the P2-06 read-only active-condition route.
- `src/SmartX.Contracts/Realtime/RealtimeActiveConditionsResponse.cs` — stable membership keys, per-episode issue IDs, counts and states.
- `tests/SmartX.Domain.Tests/Dashboard/DashboardActiveConditionSetTests.cs` — domain lifecycle and uniqueness.
- `tests/SmartX.Domain.Tests/Dashboard/DashboardActiveConditionConcurrencyRegressionTests.cs` — concurrent duplicates and recovery/recurrence.
- `tests/SmartX.Api.IntegrationTests/Dashboard/RealtimeActiveConditionsEndpointTests.cs` — real HTTP lifecycle snapshots.
- `tests/SmartX.Api.IntegrationTests/Dashboard/RealtimeActiveConditionRegressionTests.cs` — cross-feature and notification compatibility.

After the P2-06 Commit 3 validation, the full backend solution was reported **green with 592 tests passed and zero failed**.

## Data-structure rationale and limits

The `HashSet<string>` governs first activation, membership queries and removal on recovery, with *average-case* O(1) membership/add/remove complexity. The existing dictionary maps keys to issue details; it is not a competing alert membership authority. The existing lock makes membership and issue transitions atomic to application callers. An acknowledged issue remains active until genuine recovery. A later recurrence starts a new, auditable issue episode.

This system uses process-local active-condition and telemetry state. It does not claim persistent distributed coordination or cross-restart issue replay; those would require additional infrastructure outside this Part 2 scope.
