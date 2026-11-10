# Smart-X Part 3 - Assessment Traceability Matrix

**Issue:** P3-01 / baseline  
**Source:** `PROG7312POE(2).pdf`, functional requirements on pp. 10-13 and detailed marking rubric on pp. 25-28.  
**Status:** Planning and evidence targets only. Implementation and test outcomes are not claimed here.

The functional requirements on page 12 describe **20 marks for trees + 30 marks for graphs**, while the detailed Part 3 rubric on page 25 awards **30 marks for trees + 30 for graphs**. The issue plan targets the detailed rubric while covering every functional requirement.

| Criterion (detailed rubric) | Marks | Planned issues | Required implementation evidence |
| --- | ---: | --- | --- |
| Self-balancing telemetry trees | 30 | P3-02, P3-03, P3-09, P3-11 | Actual AVL nodes and LL/RR/LR/RL rotations; correct inclusive timestamp range queries; duplicate-time retention; thousands of seeded observations; verified pruning and response ordering. |
| IoT graph, traversal and minimum spanning tree | 30 | P3-04 through P3-08, P3-11 | Real sensor vertices and weighted links; BFS gateway reachability and shortest **hop** paths; Prim minimum spanning tree/forest; recalculation after a failed node or link; validated and explainable routing. |
| README and data-structure explanations | 10 | P3-12 | Up-to-date setup, API, Docker and demo instructions; actual AVL/BFS/Prim source examples; algorithm-selection and complexity explanations. |
| Project completion report | 10 | P3-13 | Detailed engineering challenges, debugging choices, integration decisions and lessons from the complete three-part application. |
| Technology recommendations | 5 | P3-13 | Practical, relevant recommendations for operating or scaling a .NET IoT ecosystem. |
| Technical justifications for recommendations | 5 | P3-13 | Specific, balanced security, scalability, operations, performance and migration rationale. |
| Part 1 and 2 feedback updates | 5 | P3-10, P3-13 | Traceable feedback ledger including the known 18/30 prediction result, actual changes, tests, evidence and Git references. |
| Main menu and architectural integration | 5 | P3-01, P3-08, P3-09, P3-11 | Three accessible working pillars, direct/Back/Forward routing, preserved selection and drafts, responsive async queries, regression across earlier parts. |
| **TOTAL** | **100** | | |

## Design distinctions to demonstrate explicitly

- **AVL vs. Part 1 deployment tree:** The historical range-query tree is a real self-balancing index, not the existing recursive validation structure.
- **AVL vs. Part 2 `SortedDictionary`:** The index belongs to accepted sensor telemetry and must support pruned time-window searches; the operational timeline has a separate purpose.
- **BFS vs. minimum spanning tree:** BFS minimises the number of hops in an unweighted graph. Prim minimises total simulated link cost across connected nodes, **not** necessarily the individual least-cost path between every device and the gateway.
- **Self-healing vs. invented connectivity:** Remove/down an actual node or link, recompute reachability and the spanning forest, and mark unreachable devices rather than drawing impossible routes.
- **Real data vs. UI fiction:** Sensors originate from the registered sensor repository. Seeded radio links are transparently labelled simulated, with finite positive costs and deterministic scenarios.

## Evidence checklist for final delivery

- [ ] Executed baseline and documented gate results (`docs/part3/BASELINE.md`).
- [ ] Domain/infrastructure tests for rotations, invariants, repeated timestamps and large input.
- [ ] API range-query proof on accepted typed telemetry; unchanged Part 1 recent-history contract.
- [ ] Graph tests for cycles, disconnected components, down nodes, deterministic BFS and Prim cost.
- [ ] Live client screenshot of topology, routes and clear simulated link metrics.
- [ ] E2E across all three pillars and responsive layouts; retained workspace state.
- [ ] Full .NET, frontend, browser and Docker regression evidence from a final release candidate.
- [ ] README with source-grounded algorithm excerpts and time-complexity discussion.
- [ ] Project completion report **as a PDF**, with specific learning/challenge reflections.
- [ ] Technology recommendations with justified compatibility and trade-offs.
- [ ] Lecturer-feedback change ledger with accurate Git commit/PR references.
- [ ] Final GitHub branch/PR history and lecturer-facing video or presentation evidence, as required.

**Delivery note:** The rubric includes a GitHub penalty risk for limited history or missing/malformed README. Meaningful per-issue commits and the final documentation are planned; do not manufacture commits for their own sake.
