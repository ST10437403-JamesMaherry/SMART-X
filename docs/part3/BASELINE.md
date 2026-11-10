# Smart-X Part 3 - Verified Baseline

**Issue:** P3-01 - Establish Part 3 baseline and activate topology navigation  
**Commit:** 1 of 3 - `chore(docs): record the verified Part 3 release baseline`  
**Purpose:** Establish a reproducible, evidence-based checkpoint **before** changing any Part 1 or Part 2 application behaviour.

## 1. Authoritative historical reference (not a live test result)

| Property | Last known released state |
| --- | --- |
| Snapshot | `SmartX_PART2_FINAL_SNAPSHOT_2026-10-19_12-41-35_adb422884438.txt` |
| Snapshot captured (SAST) | 19 October 2026, 12:41 |
| Snapshot branch | `main` |
| Snapshot commit | `adb422884438f6a12e330500b08aabb5f9842c33` |
| Release tag visible in snapshot | `2.0.0` |
| Recorded tracked files | 397 |
| Backend | .NET 10, Domain / Contracts / Application / Infrastructure / API |
| Frontend | React, TypeScript, Vite, React Router, Playwright |
| Deployment | Nginx frontend, private .NET API, Docker Compose, SignalR |

The above values describe the **19 October snapshot** only. They do not establish the present Git branch, current dependency state, or passing tests. The validation section below is populated **only** by running `scripts/capture_part3_baseline.sh` against the current local checkout.

## 2. Existing features and contracts to preserve

- **Pillar 1:** Sensor registration and update, identifiers/deployment validation, typed single and batch telemetry, ingestion-order history, anomaly spotlight and encrypted attachments.
- **Pillar 2:** FIFO and priority telemetry processing, canonical dictionary lookup, active-condition `HashSet`, ordered operational history, relay commands with per-device `Stack` Undo, search and filters, SignalR refresh, and learned Suggested Actions.
- **Navigation:** The release exposes `/`, `/ingestion`, and `/realtime`. The topology pillar is visible but disabled at this checkpoint; P3-01 Commit 2 will activate `/topology`.
- **State retention:** `WorkspaceStateProvider` retains safe selected-device and workspace state across pillar navigation; existing state must not regress.
- **Telemetry ownership:** `InMemoryTelemetryRepository` remains authoritative; a future AVL is a secondary timestamp index rather than a replacement for typed ingestion or the existing recent-history semantics.
- **Deployment boundary:** The Nginx client is the public Compose endpoint. The API stays private, and encrypted attachments use the existing named volume.
- **Data lifetime:** Sensor, telemetry, command, history and recommendation stores are process-local. Do not claim persistence after an API restart.

## 3. Known assessment concern from Part 2

The reported score for **Predictive Action and Suggestion Engine** was **18/30**. The final Part 2 snapshot nevertheless contains `PredictivePatternLearningService`, `PredictiveActionService`, `InMemoryInteractionPatternStore`, `SuggestedActions`, and related unit/integration/Playwright tests. The grading reason cannot be inferred from the score alone. P3-10 will strengthen visible proactivity and reproducible evidence, while P3-13 will record the change without inventing lecturer remarks or claiming a retroactive grade improvement.

## 4. Part 3 assessment and test gates

Refer to [`ASSESSMENT_MATRIX.md`](ASSESSMENT_MATRIX.md) for the full 100-mark breakdown, planned implementation issues and evidence. The main rubric is on pages 25-28 of `PROG7312POE(2).pdf`.

This baseline follows the existing CI's four quality areas:

1. **Backend:** audited restore, formatting, Release build, xUnit regression.
2. **Frontend:** reproducible `npm ci`, dependency audit, ESLint, Prettier, production build.
3. **Playwright:** real HTTP API + Vite + Chromium browser regression.
4. **Docker:** quiet Compose configuration and production image builds (without starting or removing any containers).

### Running the local baseline check

From the repository root, **on the Part 3 feature branch and before Commit 1**, run:

```bash
bash scripts/capture_part3_baseline.sh
```

The script runs the existing quality gates and replaces **only** the generated results section below. It records the actual branch, current base commit, tool versions, pass/fail/skipped statuses and test-output summaries. It does **not** commit, push, start/stop production containers, reveal encryption keys, or modify tracked application source files.

If a gate fails or cannot be run, it remains visible as **FAIL** or **SKIPPED**. Fix pre-existing failures or document the reason before declaring this issue's baseline green. Local execution does not substitute for the later GitHub Actions check.

<!-- P3_BASELINE_RESULTS_BEGIN -->

## 5. Current local baseline results

**PENDING - NOT YET VERIFIED.** Run the command above to capture fresh results on the current checkout. Do not commit this template with a claim that tests have passed.

<!-- P3_BASELINE_RESULTS_END -->
