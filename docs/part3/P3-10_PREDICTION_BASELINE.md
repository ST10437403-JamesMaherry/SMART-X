# P3-10 — Existing prediction engine, verified boundaries, and targeted improvements

**Status:** Commit 1 baseline and regression guardrails. The topology-aware insight service and its user interface belong to later P3-10 commits; they are **not implemented by this commit**.

**Baseline:** Post-P3-09 `dev`. The last uploaded complete source checkpoint was post-P3-08, at `6dfe8c23e2ed76a24bcd4ea535556e57c8db7264`. P3-09 then added AVL history and cross-pillar navigation; it did not replace the P2 prediction engine. Run the commands below against the actual current `dev` branch, not against that older snapshot.

## 1. Precisely what the Part 2 feedback establishes

The developer reported **18/30 for the Part 2 Predictive Action and Suggestion Engine**. No specific lecturer comments were provided to identify the reason for the deduction. It would be incorrect to claim that prediction logic was missing, that a particular algorithm was criticized, or that the previous mark will change.

The P3-10 response is to make the existing feature **more visible, proactive, explainable, and testable**, without falsely advertising a machine-learned topology detector. The feedback ledger at [`FEEDBACK_UPDATES.md`](FEEDBACK_UPDATES.md) records only observable changes and their evidence.

## 2. Existing authoritative implementation (do not replace)

| Responsibility | Source of truth | Current behaviour |
| --- | --- | --- |
| Operator evidence | `InMemoryOperatorInteractionStore` and `OperatorInteractionService` | Bounded server-timestamped history of explicitly recorded interactions; passive GETs and keystrokes are not training events |
| Source conditions | `DashboardIssueCollection` and `DashboardIssueService` | A HashSet-backed unresolved-episode identity; recovery resolves that episode |
| Pattern learning | `PredictivePatternLearningService` | Requires an actual `AlertViewed`, a later selected `SearchSubmitted` for the target, and a matching server-recorded, state-changing command inside the bounded time interval |
| Evidence retention | `InMemoryInteractionPatternStore` and `PatternLearningPolicy` | Seven-day default retention, one count per distinct alert episode, bounded Dictionary of learned keys |
| Suggestions | `PredictiveActionService` | Read-only, deterministic ranking against **current** source condition, registered actuator eligibility, and current relay state |
| Browser contract | `RealtimeRecommendationsController` and `SuggestedActions.tsx` | `GET /api/realtime/recommendations` is read-only/no-store; review re-fetches eligibility; confirmation uses the normal command endpoint |
| Command safety | `ActuatorCommandService` and `InMemoryActuatorCommandStore` | Real validated relay transactions with Stack-backed Undo; no-op and Undo do not train |

**Evidence threshold:** Three *different source-alert episodes* must support a given source/condition/severity/movement/target/command pattern before a suggestion is eligible. Clicking the same alert repeatedly does not create three independent observations.

**Important terminology:** The numeric `score` and `High`/`Moderate`/`Emerging` labels are an explicit **ranking heuristic**, not a calibrated probability, machine-learning confidence, or guarantee that a command will work.

**History is process-local:** Suggestions do not survive an API restart because the stores are in-memory. P3-10 will not silently add persistence or identity tracking.

## 3. Commit 1: newly pinned regression boundaries

`tests/SmartX.Infrastructure.Tests/Recommendations/P3_10PredictionEvidenceBoundaryTests.cs` builds the real concrete application and infrastructure services in each isolated test fixture. It does not inject fake learned counts, mock a successful relay transaction, or bypass the actual command Stack when demonstrating learning.

| Regression | Expectation | Safety property |
| --- | --- | --- |
| Generic device view plus alert-associated search | A real accepted relay command produces **zero** learned episodes until `AlertViewed` is explicitly recorded | Alert evidence must not be inferred from ordinary browsing |
| Fabricated `CommandRecord` without server `CommandExecuted` event | `ObserveAcceptedCommand` rejects it; accepted commands through `ActuatorCommandService` still learn | No fabricated success evidence |
| Two selected searches sharing the exact UTC timestamp | The **last server sequence** controls which target is eligible, not timestamp ordering or a stale selection | Search/command provenance remains correct |
| Three distinct alert episodes with genuine Undo between commands | Two episodes yield no suggestion; three yield an explained advisory action; repeated reads do not change the command or support | Genuine training, no autonomous actuation or passive learning |
| Source episode recovery after three training events | Live advice disappears, while authentic historical evidence remains bounded in the pattern store | Current-condition freshness is separate from learned history |

Existing P2 baselines also remain mandatory:

- `tests/SmartX.Infrastructure.Tests/Recommendations/PredictiveActionServiceTests.cs`
- `tests/SmartX.Infrastructure.Tests/Recommendations/PredictivePatternLearningServiceTests.cs`
- `tests/SmartX.Infrastructure.Tests/Recommendations/InMemoryInteractionPatternStoreTests.cs`
- `tests/SmartX.Api.IntegrationTests/Recommendations/PredictivePatternLearningEndpointTests.cs`
- `tests/SmartX.Api.IntegrationTests/Recommendations/PredictiveRecommendationsEndpointTests.cs`
- `src/SmartX.Client/e2e/part2-suggested-actions-journey.spec.ts`
- `src/SmartX.Client/e2e/part2-suggested-actions-execution.spec.ts`

## 4. Observed opportunity, not a proven lecturer criticism

The existing prediction contract returns **learned relay-command suggestions** only when there are sufficient genuine historical episodes and a matching current issue. It does not currently carry a separate, typed **graph-derived operational insight** about network reachability, disconnection, or recovery. Consequently, a first-visit operator cannot rely on learned suggestions alone to explain a never-before-seen mesh failure.

The Part 3 mesh already exposes server-authoritative reachability, BFS routes, a Prim MST, registered identities, links, and graph versions. A small read-only projection can use those facts to communicate *current network conditions* without pretending that the condition was learned from operator behaviour.

### Follow-on implementation boundaries

1. **Commit 2 — Current-network insight projection:** Derive bounded advisory insights from real topology and sensor state. Use deterministic keys, graph version/evaluation timestamps, explicit reasons and recovery/expiry behaviour; do not create commands or learning observations. Make first-visit failures observable without search.
2. **Commit 3 — Operator UI:** Visually and semantically separate `Live network insight (rule-derived)` from `Suggested action (learned from 3+ episodes)`. Show affected registered devices, supporting evidence, freshness and a safe next step; preserve review/confirmation for learned commands. Use real bounded interaction records only for search trends.
3. **Commit 4 — End-to-end proof:** Register and disconnect actual devices, observe an immediate explained alert without searching, verify deduplication and resolution, and independently execute the genuine P2 alert/search/command training workflow. Measure payload and refresh overhead; record observed data, not invented performance claims.

**Non-goals:** No externally hosted AI model, new machine-learning training service, speculative radio link measurements, physical energy measurements, or automatic actuator execution.

## 5. Reproducible baseline checks

From the repository root:

```bash
# Existing and new recommendation behaviour; use the current net10.0 solution.
dotnet restore SmartX.sln
dotnet build SmartX.sln -c Release --no-restore
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~Recommendations' \
  --logger 'console;verbosity=normal'
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~Recommendations' \
  --logger 'console;verbosity=normal'

# Existing real-browser demonstrated training path.
cd src/SmartX.Client
CI=true npm run test:e2e -- --grep 'P2-10' --retries=0
```

The comprehensive full-suite gates will be rerun at the end of the issue. These commands are **instructions**, not claims that the new tests already passed. Report actual results from the developer's terminal or CI before marking the feedback ledger verified.

## 6. Engineering acceptance for this commit

- Tests remain isolated and deterministic through an injected `TimeProvider`.
- Only existing concrete components are used; no application code or public API is modified.
- Every test states an evidence boundary that future live-network insights must not weaken.
- The documented feedback is explicitly identified as developer-reported, not invented lecturer commentary.
- Final P3-10 feedback status stays pending until real API/browser proof is available.
