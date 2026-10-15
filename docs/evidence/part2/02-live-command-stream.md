# Smart-X P2-07 — Live command stream dashboard and API-driven simulator

**PROG7312 Part 2 | React application evidence**

This evidence consists of **actual Chromium screenshots of the running Smart-X React UI**. The capture script drives genuine browser controls against an existing, healthy Smart-X API. Independent HTTP reads verify the resulting Domain/Application state before any screenshot is published. Screenshots are not generated illustrations or reports styled to imitate a frontend.

## Evidence files

| File | What it demonstrates |
| --- | --- |
| `02-live-command-stream.png` | Desktop operations console with process-wide accepted/processed/pending counters, ordinary/critical dispatch diagnostics, a real Actuator/Boolean device, commanded **On** state, nonempty server Undo Stack, accepted no-op, linked Undo in audit/history, and simulator controls. |
| `02-active-conditions.png` | The actual React console displaying a new Critical telemetry-spike episode, its HashSet-backed condition key/state/count, and the simulator's verified spike/recovery/recurrence results. |
| `09-responsive-part2.png` | Full-page Chromium screenshot at a genuine **390px** mobile viewport: navigation, operator console, command/history controls, and simulator. The script verifies there is no document-level horizontal overflow. |
| `09-responsive-part2-simulator.png` | Close-up *element screenshot* of the actual mobile simulator, retaining its five scenarios, finite-load inputs, and progress/result interface. |
| `02-live-command-stream-summary.json` | Verified, non-secret API facts supporting the desktop/mobile captures: actual sensor IDs, command audit/Stack, chronological event counts, recurrent active issue, worker counts, and measured viewport overflow. |
| `02-finite-load.png` | **Optional**, independently captured live React result for the finite typed workload. |
| `02-finite-load-summary.json` | **Optional**, server-confirmed device/reading acceptance and process-wide worker counts/deltas; processed and accepted counts remain explicitly distinct. |
| `src/SmartX.Client/scripts/capture-part2-evidence.mjs` | Reproducible, commented capture program using the project's existing `@playwright/test` dependency. |

## Reproduce — standard real-browser screenshots

From the repository root, start a local production-style Docker deployment (with your normal ignored `.env` configured):

```bash
# Use --quiet: full docker compose config output may reveal environment secrets.
docker compose config --quiet
docker compose up -d --build
docker compose ps
curl --fail --silent --show-error http://localhost:8080/api/health
```

The actual application is at `http://localhost:8080/realtime`. Use the configured host port instead if you changed `SMARTX_HTTP_PORT`.

From the frontend directory, install Playwright's Chromium only when missing, then run:

```bash
cd src/SmartX.Client
npx playwright install chromium
node scripts/capture-part2-evidence.mjs
```

Or, for an alternative local public port:

```bash
node scripts/capture-part2-evidence.mjs --base-url http://localhost:9090
```

The script requires a healthy worker and an unpaused ordinary lane, and it only accepts localhost/127.0.0.1 origins. It uses the built-in browser simulator to register uniquely identified **Actuator/Boolean** and **Environmental/Float** demonstration devices. Existing devices are not changed. The associated encrypted configuration attachments follow the real Part 1 upload path. The simulator and background worker own packet/command execution; the script only drives UI controls, reads the real API, and takes screenshots.

The capture verifies all these **actual** facts before writing evidence:

1. Two Boolean readings processed for a newly registered actuator.
2. RelayOn, an accepted no-op, and an audited Undo reversing the command.
3. An additional real UI RelayOn leaving the commanded relay **On**, server Undo depth **1**, four actual audit records, and the linked Undo preserved in chronological history.
4. A new Float sensor's seven-reading anomaly scenario: Warning/Critical escalation, `RepeatedFailure`, two-stable-reading recovery, and later Critical `TelemetrySpike` recurrence with occurrence count **1**.
5. The P2-06 authoritative active-condition endpoint and P2-05 chronological history agree with those new devices' state.
6. The responsive mobile viewport is **390px**, with no document-level horizontal overflow.

If a verification fails, the script exits nonzero and does not publish new evidence. Source state, pending queues, and worker processing remain owned by the server. The operation is an API demonstration, not a synthetic snapshot.

## Reproduce — optional full finite-load evidence

The default UI settings are **100 devices x 100 readings = 10,000 typed readings**. A separate opt-in run creates these devices and accepts bounded jagged batches through the real Part 1 telemetry endpoint. It is intentionally **not** part of routine Playwright CI.

Wait for the worker to be healthy with empty queues. Run this from `src/SmartX.Client`:

```bash
node scripts/capture-part2-evidence.mjs --load-only --devices 100 --readings 100
```

A fast smoke check of the **same code path** is:

```bash
node scripts/capture-part2-evidence.mjs --load-only --devices 2 --readings 5
```

**Important:** The smoke check overwrites the optional load screenshot, so run the full 100/100 capture **last** if you want to submit evidence of 10,000 readings. Run the full load only once on a quiet local demo instance. Do not restart the API in the middle of the run because the sensor, telemetry, queue, Stack, and history stores are process-local. Normal `docker compose down` preserves encrypted attachment files but does not preserve process-local device state.

The optional load capture verifies **HTTP-confirmed** registered devices and accepted typed packets using the server's batch receipts, then records the process-wide accepted, processed, and pending counters at capture time. **It does not claim that all 10,000 packets had already completed processing** at that instant, or imply a throughput or latency guarantee.

## Automated regression and rubric alignment

- `src/SmartX.Client/e2e/part2-realtime-foundation.spec.ts` — typed snapshots and navigation retention.
- `src/SmartX.Client/e2e/part2-realtime-live.spec.ts` — real SignalR invalidation, commands, Undo, history, active conditions.
- `src/SmartX.Client/e2e/part2-realtime-simulator.spec.ts` — normal, spike, actuator, disconnect, priority bypass, and small finite-load scenarios.
- `src/SmartX.Client/e2e/part2-realtime-reliability.spec.ts` — stale/failed HTTP handling, reconnect, device isolation, Stop, invalid limits, retry.
- `src/SmartX.Client/e2e/part2-realtime-responsive.spec.ts` — desktop/tablet/mobile widths, keyboard/focus, reduced motion, and recovery.
- `src/SmartX.Client/e2e/smartx.spec.ts` and `part2-navigation.spec.ts` — original Part 1 and pillar navigation regression.

**Main Menu Navigation (30 marks):** real React pillar integration with retained device selection and tested navigation. **Collections (15 marks):** actual FIFO/critical work, command Stack/Undo, and SortedDictionary chronological history visible in the UI. **Lookup Structures (15 marks):** canonical registered-device selection from backend-projected Dictionary-backed state. **Sets (10 marks):** one active issue per sensor/type and server-driven recovery/recurrence. **Mock data volume:** the optional 100/10,000 run uses the same real registration/typed-batch APIs as the UI, not a fabricated load counter.

## Scope and safety

- Images show **actual UI** after verified operations; they are not the standalone P2-03/P2-06 API-rendered evidence reports.
- No passwords, bearer tokens, `.env` contents, or encryption keys are captured by the script.
- Do not expose the application to the public internet for evidence capture.
- An in-memory API restart removes registered sensors and clears worker/command/alert history; re-run capture against a fresh process to reproduce the display.
- Perform frontend, Playwright, backend, and Docker CI checks before merging into `dev`.
