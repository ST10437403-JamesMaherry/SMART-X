# Smart-X Part 3 — Lecturer feedback response ledger

**Status:** Baseline recorded in P3-10 Commit 1; Commit 2 implements a separate, read-only topology-insight service. Do not mark the remediation complete until local tests and the operator-facing browser demonstration have been verified. This ledger will be updated in subsequent P3-10 and P3-13 commits.

## Evidence rules

- Record only feedback actually supplied by the developer or lecturer.
- Distinguish earlier marks from **new** Part 3 evidence; no earlier mark is retroactively changed.
- Record exact changed repository files, test names and Git commit IDs only after they exist.
- Preserve the separate provenance of learned responses and live, rule-derived network alerts.
- Do not claim screenshots, payload measurements, GitHub CI outcomes or a live demonstration before they have been produced and reviewed.

## P2-FB-01 — Prediction and suggestion-engine improvement

| Field | Current record |
| --- | --- |
| Origin | Developer-reported Part 2 result |
| Assessment area | Predictive Action and Suggestion Engine |
| Reported mark | **18/30** |
| Exact lecturer comment | **Not provided**; no particular defect has been established by lecturer evidence |
| Existing verified design | P2-09 evidence-based, episode-deduplicated learning; P2-10 operator-reviewed Suggested Actions and normal command/Undo execution |
| Proposed improvement | Add genuinely proactive, explained network-risk insights using current server topology, clearly separate them from learned commands, and show bounded authentic search context |
| Current status | **In progress** — baseline regression and rule-derived topology API added; user interface and final browser proof pending |
| Initial code change | `tests/SmartX.Infrastructure.Tests/Recommendations/P3_10PredictionEvidenceBoundaryTests.cs` |
| Initial design/evidence record | `docs/part3/P3-10_PREDICTION_BASELINE.md` |
| Initial commit | `test(recommendations): baseline genuine learning and feedback gaps` — **SHA to be recorded after Git commit** |
| Test output | **Pending local execution**; do not substitute source inspection for an actual green test run |
| Next evidence | P3-10 Commit 2 service unit/API checks; Commit 3 UI integration; Commit 4 genuine first-visit failure and separately trained action workflow |
| Final status and mark | Pending. No claim of reassessment or changed Part 2 mark |

### Traceable checkpoints

| Phase | Expected evidence | Verified result |
| --- | --- | --- |
| C1 — baseline | Existing learning, command/Undo and new provenance-focused regression tests | Pending developer run |
| C2 — live graph | Actual registered sensors, versioned BFS reachability, stable alert keys, recovery expiry and cooldown | **Implemented in source, pending developer test output**; `TopologyInsightService.cs`, `TopologyInsightsResponse.cs`, `/api/topology/insights`, unit and HTTP tests; `P3-10_TOPOLOGY_INSIGHTS_API.md` |
| C3 — operator display | Visible provenance and freshness; learned and rule-derived cards distinguished; no hidden commands | Not yet implemented |
| C4 — first-visit proof | Real API graph failure creates insight without search; recovery removes it; genuine learning journey still works | Not yet implemented |
| P3-13 — final submission | Actual commit SHAs, green CI, screenshots, feedback/report cross-references | Not yet verified |

## Part 1 feedback

No specific Part 1 lecturer feedback has been supplied for this ledger. Do not invent Part 1 problems or claim to have resolved them.

## How to complete this ledger later

Replace `Pending` only with verifiable command output, CI links, test artifacts, screenshots, and committed source paths. The final report must describe the implemented change as an **improvement in transparency, proactive behaviour and evidence**, not as proof of the unknown reason for the original 18/30 assessment.

## Commit 2: bounded operational risk projection (implementation record)

**Source changes (not a verified lecturer finding):**

- `src/SmartX.Application/Topology/TopologyInsightService.cs` — version-aware BFS and sensor-status rules; first-visit risk detection, stable current-alert identities, transient recovery expiry/cooldown; strictly advisory.
- `src/SmartX.Contracts/Topology/TopologyInsightsResponse.cs` — typed, bounded evidence and affected registered identities.
- `src/SmartX.Api/Controllers/MeshTopologyController.cs` and `src/SmartX.Api/Program.cs` — GET `/api/topology/insights`, `no-store` cache header, singleton process-local state.
- `tests/SmartX.Application.Tests/Topology/TopologyInsightServiceTests.cs` and `tests/SmartX.Api.IntegrationTests/Topology/TopologyInsightsEndpointTests.cs` — deterministic service assertions and real HTTP with sensor/link mutations.
- `docs/part3/P3-10_TOPOLOGY_INSIGHTS_API.md` — provenance, limits, semantics, safety and reproduction.

**Verification pending:** Run the targeted .NET filters and full solution suite; record actual outcomes. No claim of live browser visibility or improved Part 2 marks is made until Commits 3 and 4 are verified.


## Commit 3: accessible explanation and authentic operator evidence

**Implemented (not yet independently verified):**

- `src/SmartX.Client/src/features/topology/TopologyInsights.tsx` and `src/api/topologyInsightsApi.ts`: render bounded current BFS/status conditions with graph version, origin, observed timestamps, real affected sensor IDs, recommended next checks and recovery expiry. No invented prediction confidence or physical network data.
- `src/SmartX.Client/src/features/realtime/RealtimeWorkspace.tsx`: show current topology warnings in Operations using its existing hub connection, with only topology-related invalidations triggering new insight evaluations.
- `src/SmartX.Client/src/features/realtime/RecentOperatorSearches.tsx`: display real persisted search submissions and small frequency ranking from the bounded interaction endpoint, without pretending this is a trained actuator suggestion.
- `src/SmartX.Client/src/features/realtime/SuggestedActions.tsx`: preserve historical evidence-based command review, Undo and prior E2E selectors; clarify these are learned episodes rather than live mesh rules.
- `docs/part3/P3-10_EXPLAINABLE_INSIGHTS_UI.md`: implementation and safety rationale.

**Verification pending:** locally run ESLint, Prettier, build and browser tests after Commit 4. These improvements do not demonstrate the unknown reason for the previous 18/30 mark or imply it was changed.
