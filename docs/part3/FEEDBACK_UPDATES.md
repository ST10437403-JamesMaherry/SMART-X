# Smart-X Part 3 — Lecturer feedback response ledger

**Status:** Initial ledger recorded in P3-10 Commit 1. Do not mark the remediation complete until the live-insight implementation and the end-to-end evidence have been verified. This ledger will be updated in subsequent P3-10 and P3-13 commits.

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
| Current status | **In progress** — baseline and safety regression work only |
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
| C2 — live graph | Real registered sensors, versioned reachability-based insight, stale/resolved suppression | Not yet implemented |
| C3 — operator display | Visible provenance and freshness; learned and rule-derived cards distinguished; no hidden commands | Not yet implemented |
| C4 — first-visit proof | Real API graph failure creates insight without search; recovery removes it; genuine learning journey still works | Not yet implemented |
| P3-13 — final submission | Actual commit SHAs, green CI, screenshots, feedback/report cross-references | Not yet verified |

## Part 1 feedback

No specific Part 1 lecturer feedback has been supplied for this ledger. Do not invent Part 1 problems or claim to have resolved them.

## How to complete this ledger later

Replace `Pending` only with verifiable command output, CI links, test artifacts, screenshots, and committed source paths. The final report must describe the implemented change as an **improvement in transparency, proactive behaviour and evidence**, not as proof of the unknown reason for the original 18/30 assessment.
