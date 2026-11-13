# Smart-X Part 3 - Lecturer Feedback Update Ledger

**Status:** Final Part 3 engineering response record
**Project:** Smart-X IoT Mesh Ecosystem
**Module:** PROG7312 Programming 3B
**Purpose:** Record the changes made in response to feedback and assessment results from earlier Smart-X submissions without inventing lecturer comments or changing historical marks.

---

## 1. Evidence rules

This ledger follows these rules:

- Only feedback or marks actually supplied are recorded.
- Historical marks are not changed or reinterpreted.
- No missing lecturer comments are invented.
- Part 3 improvements are described using actual source, tests, documentation and demonstrations.
- Learned Suggested Actions and rule-derived topology insights are treated as different features.
- Current network observations are not presented as machine learning.
- Test totals are taken from completed regression evidence.
- Git commit identifiers are included only when they are known.
- Simulated topology costs are treated as relative demonstration values, not physical RF or measured energy values.

---

## 2. Part 1 feedback record

No specific Part 1 lecturer feedback was supplied for this ledger.

For that reason, Smart-X does not claim that a particular Part 1 problem was identified by the lecturer or that a specific Part 1 mark was recovered.

Part 1 functionality was instead treated as a compatibility baseline throughout Parts 2 and 3.

The completed application continues to retain:

- sensor registration and profile management
- strongly typed Float, Integer and Boolean telemetry
- single-reading and jagged historical batch ingestion
- ingestion-order recent telemetry
- recursive deployment validation
- anomaly detection and the Adaptive Anomaly Spotlight
- sensor operational-state simulation
- encrypted sensor attachments
- API validation and ProblemDetails responses
- Docker deployment through the existing Nginx and ASP.NET Core architecture

These features were repeatedly covered by the full regression suite while Parts 2 and 3 were added.

### Part 1 update status

| Area | Final record |
| --- | --- |
| Specific lecturer feedback supplied | No |
| Invented feedback added | No |
| Existing functionality retained | Yes |
| Regression coverage retained | Yes |
| Replaced by Part 3 structures | No |
| Current role | Preserved first Smart-X pillar |

---

## 3. Part 2 assessment result used for improvement

The supplied Part 2 assessment result identified one area that required extra attention.

| Field | Recorded result |
| --- | --- |
| Assessment area | Predictive Action and Suggestion Engine |
| Reported Part 2 mark | **18/30** |
| Exact lecturer comment | **Not supplied** |
| Confirmed reason for deduction | Unknown |
| Retroactive mark change claimed | No |
| Part 3 response | Improve visibility, proactivity, provenance and verification while retaining the genuine learned-action workflow |

The score alone does not prove that the Part 2 implementation was missing, incorrect, insufficiently proactive, poorly demonstrated, or affected by any particular algorithmic problem.

Smart-X therefore did not replace the existing prediction engine based on an assumed cause.

Instead, Part 3 strengthened the areas that could be improved objectively and demonstrated clearly.

---

# 4. Existing Part 2 behaviour that was preserved

Before the Part 3 response, Smart-X already contained an evidence-based Suggested Actions workflow.

The implementation includes:

- bounded operator interaction history
- explicit `AlertViewed` evidence
- deliberate submitted searches
- server-recorded command outcomes
- learned pattern support across separate alert episodes
- current actuator eligibility checks
- explained Suggested Actions
- operator review before execution
- the normal validated command API
- Stack-backed Undo

A recommendation requires genuine supporting behaviour. Passive page refreshes and ordinary GET requests do not create training evidence.

The existing learned workflow remained intact during Part 3.

This was important because improving the visibility of the feature should not weaken the genuine learning behaviour already implemented.

---

# 5. Improvement 1 - Pin the learning evidence boundaries

## Goal

Make it harder for accidental or fabricated interactions to appear as genuine evidence for a learned recommendation.

## Main implementation

```text
tests/SmartX.Infrastructure.Tests/Recommendations/
P3_10PredictionEvidenceBoundaryTests.cs
```

The P3-10 regression tests verify boundaries around the original prediction workflow.

Examples include:

- a normal device view cannot replace an explicit alert-associated interaction
- an arbitrary client-created command record cannot fabricate a successful server command
- repeated activity inside one alert episode cannot falsely create several independent learning episodes
- server sequence order is used where timestamp equality could otherwise make evidence ambiguous
- genuine Undo behaviour remains separate from ordinary learning
- recovery removes current recommendation eligibility while retaining bounded historical evidence

## Result

The existing Part 2 prediction feature was preserved, while its evidence rules became easier to verify.

The improvement did not change the historical 18/30 mark and does not claim to identify the reason for that mark.

---

# 6. Improvement 2 - Add proactive current-network observations

## Goal

Provide useful operational information before an operator has to search for a device or build enough history for a learned Suggested Action.

## Main implementation

```text
src/SmartX.Application/Topology/TopologyInsightService.cs
src/SmartX.Contracts/Topology/TopologyInsightsResponse.cs
src/SmartX.Api/Controllers/MeshTopologyController.cs
src/SmartX.Api/Program.cs
```

Endpoint:

```text
GET /api/topology/insights
```

The new service reads the current authoritative topology and uses the existing BFS routing implementation.

It can identify conditions such as:

- registered forwarding-eligible sensors that cannot reach the gateway
- configured sensors that are currently in non-forwarding operational states
- restoration of previously disconnected gateway connectivity

The service does not:

- train from searches
- issue device commands
- create links
- change sensor state
- modify telemetry
- create prediction confidence values
- claim physical radio measurements

## Why this improved Smart-X

The Part 2 Suggested Actions workflow requires genuine historical operator evidence.

That is useful for learned actions, but it should not prevent Smart-X from reporting an obvious current network problem.

The topology insight service therefore adds a separate form of proactive guidance based only on the current graph and sensor state.

This gives the operator immediate information while preserving the integrity of the learned recommendation engine.

---

# 7. Improvement 3 - Make provenance visible in the UI

## Goal

Make it obvious to the operator why a recommendation or warning exists.

## Main client files

```text
src/SmartX.Client/src/features/topology/TopologyInsights.tsx
src/SmartX.Client/src/api/topologyInsightsApi.ts
src/SmartX.Client/src/features/realtime/RealtimeWorkspace.tsx
src/SmartX.Client/src/features/realtime/RecentOperatorSearches.tsx
src/SmartX.Client/src/features/realtime/SuggestedActions.tsx
```

The client now keeps two forms of guidance visibly separate.

### Current network insight

Presented as:

```text
Rule-derived
Not learned
```

It is supported by the current:

- graph revision
- BFS reachability state
- sensor health
- affected registered sensor identities
- server evaluation time

### Learned Suggested Action

Supported by:

- genuine previous alert episodes
- deliberate operator searches
- accepted commands
- current target eligibility
- bounded learned pattern evidence

A learned action still requires operator review.

No actuator command is executed merely because a suggestion or network warning exists.

---

# 8. Improvement 4 - Prove first-visit proactive behaviour

## Goal

Demonstrate that the new current-network guidance does not depend on a previous search or fabricated browser state.

## Browser evidence

```text
src/SmartX.Client/e2e/part3-proactive-network-insights.spec.ts
```

This workflow uses real API operations to:

1. register real Smart-X sensors
2. create simulated mesh links
3. create a reachable network
4. change a relay to a non-forwarding state
5. observe a downstream gateway disconnection
6. show the network insight without first searching for that device
7. verify the affected registered identity
8. verify the graph revision and evidence source
9. restore network connectivity
10. verify that the current condition clears
11. verify responsive behaviour
12. verify that passive insight reads do not create operator-learning evidence

The original learned-action browser journey remains separate:

```text
src/SmartX.Client/e2e/part2-suggested-actions-journey.spec.ts
```

That workflow continues to prove:

- separate genuine alert episodes
- deliberate search evidence
- successful command evidence
- the support threshold for a learned recommendation
- a later recommendation without fabricating another training search
- explicit operator review
- command execution through the normal command path
- audited Undo

---

# 9. Improvement 5 - Keep current topology and learned behaviour separate

This distinction became an explicit Smart-X design rule.

| Feature | Live Network Insights | Suggested Actions |
| --- | --- | --- |
| Evidence source | Current graph and sensor state | Historical operator episodes |
| Uses BFS | Yes | No |
| Requires prior searches | No | Yes, as part of genuine learning evidence |
| Requires previous commands | No | Yes |
| Learns from behaviour | No | Yes |
| Has learned support count | No | Yes |
| Executes automatically | No | No |
| Can recommend operator investigation | Yes | Yes |
| Can propose an actuator command | No | Yes |
| Requires operator confirmation for command | Not applicable | Yes |

This prevents Smart-X from presenting a deterministic graph rule as if it were a learned prediction.

It also prevents a learned Suggested Action from being presented as a guaranteed outcome.

---

# 10. Improvement 6 - Preserve all three Smart-X pillars

The feedback response was implemented without replacing or bypassing the earlier system.

The final application keeps three integrated pillars:

```text
Sensor Data Ingestion and Telemetry
Real-Time Command Stream and History
Network Topology and Mesh Routing
```

Shared selected-device state allows the operator to move between these workflows without creating duplicate client-side sensor identities.

Examples include:

- selecting a sensor in Topology and opening its telemetry workflow
- opening the same device in Real-Time Operations
- returning to the selected device's AVL history
- preserving safe range-query drafts during navigation
- using browser Back and Forward without re-registering the device
- retaining authoritative server state rather than fabricating state in React

This final integration also protects the Part 1 and Part 2 work from becoming isolated demonstrations.

---

# 11. Final verification evidence

P3-11 performed the final full-system regression after the P3-10 improvements.

The final verified Part 3 release candidate produced the following results:

| Verification area | Final result |
| --- | --- |
| Domain tests | 249 passed |
| Application tests | 313 passed |
| Infrastructure tests | 230 passed |
| API integration tests | 314 passed |
| **Backend total** | **1106 passed, 0 failed** |
| Playwright | **99 passed** |
| Intentional Playwright skip | **1** |
| Playwright failures | **0** |
| Release build | Passed |
| .NET formatting | Passed |
| npm install | Passed |
| npm high-severity audit gate | Passed |
| ESLint | Passed |
| Prettier | Passed |
| Client production build | Passed |
| Docker configuration | Passed |
| Docker image build | Passed |
| API container | Healthy |
| Nginx/client container | Healthy |
| `/healthz` through Nginx | HTTP 200 |
| `/api/health` through Nginx | HTTP 200 |
| `/ingestion` | HTTP 200 |
| `/realtime` | HTTP 200 |
| `/topology` | HTTP 200 |
| topology API proxy routes | HTTP 200 |
| SignalR negotiation | HTTP 200 |
| WebSocket transport | Advertised |

The single skipped browser case is the opt-in 10,002-reading AVL browser load scenario. It is intentionally excluded from ordinary regression runs because the dedicated load scripts provide the controlled Part 3 scale evidence.

Final regression evidence:

```text
docs/evidence/part3/p3-11-final-regression.json
docs/part3/P3-11_FINAL_REGRESSION.md
```

Authoritative P3-11 merge checkpoint:

```text
47c2b1e6105830ceb8673c2c03bffc34f382b194
```

---

# 12. Load and scale evidence supporting the final changes

The final Part 3 validation also verified that the new work did not depend only on tiny examples.

## AVL telemetry

Dedicated verification covered:

```text
10,000 primary Float readings
50,000 primary Float readings
```

The checks included:

- all accepted readings accounted for
- AVL invariants
- timestamp range queries
- duplicate timestamps
- composite cursor pagination
- typed Integer and Boolean histories
- preserved Part 1 recent-history ordering
- invalid query rejection

Evidence:

```text
docs/evidence/part3/telemetry-10k.json
docs/evidence/part3/telemetry-50k.json
```

## Mesh routing

Dedicated verification covered:

```text
256 registered mesh devices
1,000 registered mesh devices
```

The checks included:

- gateway reachability
- disconnected components
- Prim selected-tree costs
- independent Kruskal cross-checking
- failure and restoration
- backup gateway routing
- complete gateway loss
- invalid topology mutation rejection

Evidence:

```text
docs/evidence/part3/mesh-256.json
docs/evidence/part3/mesh-1000.json
```

These results provide scale evidence for the completed Part 3 architecture.

They are not production service-level objectives.

---

# 13. Documentation improvements

P3-12 replaced the earlier Part 1 and Part 2 focused README with documentation for the completed three-pillar system.

The final documentation includes:

```text
README.md
src/SmartX.Client/README.md
docs/part3/ARCHITECTURE_AND_DATA_STRUCTURES.md
docs/part3/ASSESSMENT_RUBRIC_MATRIX.md
docs/evidence/part3/README.md
```

The documentation now explains:

- complete local setup
- Docker Compose setup
- the three application pillars
- telemetry ingestion
- AVL indexing
- AVL balancing
- range-query pruning
- BFS routing
- disconnected-component classification
- Prim MST routing
- BFS and Prim objective differences
- self-healing behaviour
- current network insights
- learned Suggested Actions
- real implementation limitations
- benchmark evidence
- test commands
- rubric traceability

The updated Part 3 documentation was present on `dev` at:

```text
a61addf
```

after the P3-12 documentation merge.

No additional commit SHA is guessed where it has not been supplied.

---

# 14. Final demonstration evidence

A final Part 3 video demonstration has been recorded and added to the root README.

The demonstration covers the completed Smart-X application rather than a mocked interface.

The demonstrated areas include:

- Docker-based application startup
- Sensor Data Ingestion and Telemetry
- configuration attachment handling
- telemetry submission
- historical telemetry
- AVL-backed range querying
- Network Topology and Mesh Routing
- BFS fewest-hop routing
- Prim minimum-total-cost backbone
- simulated relay failure
- rerouting through an existing backup path
- honest disconnected-device reporting
- Live Network Insights
- Real-Time Operations
- command execution and Undo
- the relationship between the three Smart-X pillars

The video link is maintained in:

```text
README.md
```

The README is treated as the single source for the final published URL so that this ledger does not duplicate a link that could later change.

---

# 15. Traceability of the Part 2 response

| Checkpoint | Engineering response | Final evidence |
| --- | --- | --- |
| C1 - Learning baseline | Preserve genuine episode-based learning and add evidence-boundary regression | P3-10 prediction boundary tests plus final regression |
| C2 - Live graph | Add current BFS and sensor-health based topology observations | `TopologyInsightService.cs`, typed API response and endpoint tests |
| C3 - Operator display | Separate rule-derived insights, search history and learned Suggested Actions | Topology and Operations client components |
| C4 - First-visit behaviour | Create network warning without requiring a prior search | `part3-proactive-network-insights.spec.ts` |
| C5 - Recovery | Remove current warning after actual gateway connectivity is restored | topology insight service and browser regression |
| C6 - Existing learned workflow | Keep real multi-episode Suggested Actions and normal command/Undo path | `part2-suggested-actions-journey.spec.ts` |
| C7 - Scale | Verify completed AVL and mesh implementation at larger sizes | P3-11 telemetry and mesh evidence |
| C8 - Full regression | Confirm earlier Smart-X functionality remains compatible | 1106 backend tests, 99 Playwright passes, Docker verification |
| C9 - Documentation | Make implementation, limitations and assessment evidence easy to inspect | P3-12 README and architecture documents |
| C10 - Demonstration | Record final working three-pillar system | Part 3 video linked in root README |

---

# 16. Final assessment position

The reported Part 2 Predictive Action and Suggestion Engine result remains:

```text
18/30
```

This ledger does not claim that the mark has been changed.

It also does not claim to know why the lecturer deducted the missing marks because no detailed comment was supplied.

The Part 3 response instead records what was actually improved:

1. genuine learning evidence was protected by additional regression boundaries
2. current network risks can now appear proactively without training history
3. rule-derived and learned guidance have explicit provenance
4. real operator search evidence is visible without fabricating training events
5. actuator suggestions remain operator reviewed
6. network observations never execute commands automatically
7. recovery and stale-state behaviour are tested
8. the complete response is covered by the final backend and browser regression
9. the final three-pillar architecture is documented
10. the completed behaviour is demonstrated in the Part 3 video

This is an engineering improvement record, not a retrospective reinterpretation of the Part 2 assessment.

---

# 17. Final ledger status

| Item | Status |
| --- | --- |
| Part 1 specific lecturer feedback supplied | No |
| Part 1 compatibility preserved | Verified |
| Part 2 prediction mark recorded | Yes, 18/30 |
| Exact Part 2 lecturer comment supplied | No |
| Existing learned workflow retained | Verified |
| P3-10 prediction evidence boundaries | Complete |
| P3-10 topology insight service | Complete |
| P3-10 provenance-aware UI | Complete |
| P3-10 first-visit proactive browser proof | Complete |
| Rule-derived and learned guidance separated | Complete |
| P3-11 final backend regression | 1106 passed |
| P3-11 final browser regression | 99 passed, 1 intentional skip |
| P3-11 Docker deployment verification | Passed |
| P3-12 final architecture documentation | Complete |
| Part 3 video demonstration | Recorded and linked in README |
| Historical mark changed or reinterpreted | No |
| Invented lecturer feedback | None |

---

## Conclusion

Smart-X used the available Part 2 assessment result as a reason to strengthen the visibility, proactivity and evidence around operational recommendations.

The final implementation preserves the genuine learned Suggested Actions workflow while adding a separate current-network insight system based on the real topology and BFS reachability state.

This approach improves the final application without pretending that current graph rules are machine learning, without fabricating lecturer comments, and without claiming that the original Part 2 mark has changed.

The result is a more transparent and verifiable Smart-X system with clear evidence across source code, automated tests, load verification, documentation and the final demonstration.
