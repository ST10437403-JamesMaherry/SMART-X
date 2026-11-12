# Smart-X Part 3 — Final Rubric Traceability Matrix

**Issue:** P3-12 — final README and technical documentation  
**Rubric source:** `PROG7312POE.pdf`, Part 3 task requirements (pp. 10–13) and detailed marking grid (pp. 25–27).  
**Source baseline:** clean `dev` snapshot `47c2b1e6105830ceb8673c2c03bffc34f382b194` after P3-11.

This matrix identifies where the final Smart-X implementation and evidence address each criterion. It is **not a promise of the marker's awarded result**. Items whose formal submission artifact belongs to P3-13 are explicitly marked as such rather than being claimed early.

[Root README](../../README.md) · [Architecture/data structures](ARCHITECTURE_AND_DATA_STRUCTURES.md) · [Part 3 evidence](../evidence/part3/README.md) · [Feedback ledger](FEEDBACK_UPDATES.md)

---

## 100-mark mapping

| Detailed Part 3 criterion                                         |   Marks | Current status after P3-12                     | Smart-X implementation / evidence                                                                                                                                                                                                                                                                                                        | Marker demonstration                                                                                                                                          |
| ----------------------------------------------------------------- | ------: | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Tree Structures and Historical Telemetry Queries**              |  **30** | **Implemented + verified**                     | `AvlTelemetryIndex<T>` with LL/RR/LR/RL rotations, height/balance invariants, UTC keys, stable duplicate buckets, pruned inclusive range traversal and keyset paging. Secondary index is wired to accepted typed telemetry without replacing Part 1 recent history. Large real-API evidence: `telemetry-10k.json`, `telemetry-50k.json`. | Register/ingest a sensor; call `/api/sensors/{id}/telemetry/range`; inspect `AvlTelemetryIndex.cs`; review 10k/50k evidence and Domain/API tests.             |
| **Graphs, Network Traversals and Minimum Spanning Trees**         |  **30** | **Implemented + verified**                     | Versioned adjacency-list mesh; registered sensors/gateway as vertices; explicit undirected simulated-cost links; BFS `Queue`/`HashSet`; Prim `.NET PriorityQueue`/`HashSet`; disconnected components; link/status rerouting; independent Kruskal cross-check in P3-11.                                                                   | Open `/topology`; read reachability/MST; run greenhouse demo; review 256/1000-node evidence and route changes after gateway-link removal.                     |
| **Implementation Report: README and Structure Code Explanations** |  **10** | **Completed in P3-12**                         | Final root README, client README, this rubric matrix, architecture document, exact source excerpts, Mermaid diagrams, setup/API commands, complexity/limits and evidence index.                                                                                                                                                          | Follow README from clean clone; inspect exact AVL/BFS/Prim excerpts and complexity section; reproduce one algorithm scenario.                                 |
| **Project Completion Report: Challenges and Learnings**           |  **10** | **Formal P3-13 deliverable pending**           | P1/P2/P3 issue docs already preserve detailed debugging/architecture decisions; P3-12 cross-links them. P3-13 must assemble the required final analytical report/PDF rather than duplicating it prematurely here.                                                                                                                        | P3-13 final PDF should link challenges to concrete API/UI/algorithm work, tests and commits.                                                                  |
| **Technology Recommendations and Future Scaling**                 |   **5** | **Formal P3-13 deliverable pending**           | P3-12 documents current limits: process-local state, local simulation, single API process, named-volume attachments, HTTP refresh/SignalR invalidation. P3-13 recommendations must respond to these real boundaries.                                                                                                                     | Final report should recommend concrete compatible tools/services and migration stages.                                                                        |
| **Technology Recommendations: Rationale**                         |   **5** | **Formal P3-13 deliverable pending**           | Architecture/limits in P3-12 provide the factual baseline for security, scalability, operations and performance rationale.                                                                                                                                                                                                               | Final report must explain benefits, risks, compatibility and why each recommendation is preferable to unnecessary complexity.                                 |
| **Iterative Feedback Incorporation**                              |   **5** | **Implemented; final ledger closure in P3-13** | `FEEDBACK_UPDATES.md` records the known Part 2 prediction result (**18/30**) without inventing a lecturer comment. P3-10 added separate proactive topology-risk insights, bounded authentic search evidence and retained the genuine learned Suggested Actions workflow.                                                                 | Show proactive network condition on first visit, recovery, separate learned action workflow, and ledger with final commit/CI references once P3-13 closes it. |
| **Main Menu Integration and Architecture Health**                 |   **5** | **Implemented + verified**                     | `/ingestion`, `/realtime`, `/topology` are active; selected device and safe drafts survive asynchronous navigation; direct/reload/Back/Forward and narrow layouts are tested; final P3-11 regression covers earlier parts and Docker routes.                                                                                             | Navigate all three pillars in one document, retain selected sensor, trigger/recover topology read failure, review P3-11 integrated regression.                |
| **TOTAL**                                                         | **100** |                                                |                                                                                                                                                                                                                                                                                                                                          |                                                                                                                                                               |

---

# 1. Tree structures — why the implementation targets the highest band

The detailed rubric's highest band expects a genuinely self-balancing tree executing time-window history queries efficiently on extensive histories.

Smart-X uses a custom AVL index rather than a decorative tree:

- accepted `TelemetryPacket<T>` instances are indexed by UTC capture timestamp;
- tree nodes maintain height and rebalance through LL/RR/LR/RL rotations;
- duplicate timestamps are stored in stable buckets;
- range traversal prunes branches outside the requested interval;
- keyset continuation uses timestamp + sequence so equal-time readings are not silently lost;
- `MaxRangeLimit = 10_000` prevents an unbounded per-request copy;
- Domain tests verify structural invariants; API tests verify transport/validation/legacy compatibility;
- P3-11 runs real API histories at 10k and 50k primary scale.

### Complexity demonstrated in the implementation

- insertion: `O(log n)` in distinct timestamp keys;
- range: `O(log n + k)` for `k` returned packets in the balanced-tree model;
- index storage: `O(n + p)` for nodes + packet buckets.

**Inspect:**

- `src/SmartX.Domain/Telemetry/AvlTelemetryIndex.cs`
- `src/SmartX.Infrastructure/Telemetry/InMemoryTelemetryRepository.cs`
- `src/SmartX.Api/Controllers/TelemetryController.cs`
- `tests/SmartX.Domain.Tests/Telemetry/`
- `docs/part3/P3-02_AVL_VERIFICATION.md`
- `docs/part3/P3-03_RANGE_QUERY_API.md`
- `docs/part3/P3-09_AVL_HISTORY_DESIGN.md`

**Evidence:**

- `docs/evidence/part3/telemetry-10k.json`
- `docs/evidence/part3/telemetry-50k.json`
- `docs/part3/LOAD_VERIFICATION.md`

---

# 2. Graphs, traversal and MST — why the implementation targets the highest band

The topology model is a real undirected weighted graph. It does not convert the UI diagram into graph truth.

### Graph

- vertices come from the authoritative gateway + registered sensor repository;
- configured links are explicit undirected edges;
- link costs are positive finite simulated weights;
- store mutations are synchronized and produce monotonic graph revisions;
- forwarding eligibility follows current authoritative sensor status.

### BFS

`MeshTraversalService` uses:

- `Queue<MeshNodeKey>` for FIFO level traversal;
- `HashSet<MeshNodeKey>` for one discovery/visit per vertex;
- predecessor dictionary for fewest-hop route reconstruction;
- component scans for forwarding-eligible disconnected islands.

Complexity: `O(V + E)` time and space.

### Prim

`MeshSpanningTreeService` uses:

- `.NET PriorityQueue<FrontierEdge, FrontierPriority>` as the min-heap;
- `HashSet<MeshNodeKey>` for selected vertices/cycle prevention;
- only explicitly configured, forwarding-eligible edges;
- deterministic tie fields after cost;
- a gateway-rooted spanning tree over the gateway-connected component.

Heap work is documented as `O(E log E)` in the implemented edge-frontier form, plus deterministic result ordering. Memory is `O(V + E)`.

### Failure and independent validation

P3-11 runs two generated graphs through the **real API**:

- 256 sensors: 231 connected + 25 island initially;
- 1,000 sensors: 900 connected + 100 island initially;
- bridge mutation connects the island;
- removing primary gateway edge forces configured backup rerouting;
- removing every gateway edge produces complete disconnection;
- restoring the primary gateway path returns connectivity;
- independent Kruskal calculations match the production Prim total;
- invalid duplicate/cost/route operations do not mutate graph version/link count.

**Inspect:**

- `src/SmartX.Infrastructure/Topology/InMemoryMeshTopologyStore.cs`
- `src/SmartX.Application/Topology/MeshTraversalService.cs`
- `src/SmartX.Application/Topology/MeshSpanningTreeService.cs`
- `src/SmartX.Api/Controllers/MeshTopologyController.cs`
- `src/SmartX.Client/src/features/topology/`

**Evidence:**

- `docs/evidence/part3/mesh-256.json`
- `docs/evidence/part3/mesh-1000.json`
- `docs/part3/MESH_LOAD_VERIFICATION.md`
- `docs/part3/P3-05_BFS_VERIFICATION.md`
- `docs/part3/P3-06_MST_OPTIMALITY_VERIFICATION.md`
- `docs/part3/P3-06_REROUTING_SCENARIOS.md`

---

# 3. README / implementation explanation — P3-12 deliverables

P3-12 provides:

- root `README.md` covering the complete three-pillar application;
- `src/SmartX.Client/README.md` replacing the generic Vite template;
- `docs/part3/ARCHITECTURE_AND_DATA_STRUCTURES.md` with exact implementation excerpts;
- this final rubric matrix;
- expanded `docs/evidence/part3/README.md` with measured fixtures and reproduction steps.

The docs explicitly distinguish:

- AVL history vs recursive Part 1 deployment tree;
- AVL history vs Part 2 `SortedDictionary` operational history;
- BFS fewest-hop objective vs Prim global MST objective;
- simulated link cost vs real RF/energy measurement;
- self-healing recomputation vs invented connectivity;
- source-verified complexity vs local observed request timings.

---

# 4. Completion report handoff to P3-13

The rubric requires a deeply analytical final report, not a generic list of programming concepts. The repository already contains the concrete source material P3-13 should synthesize:

| Challenge / learning source                 | Concrete topic                                                                   |
| ------------------------------------------- | -------------------------------------------------------------------------------- |
| `P3-03_RANGE_QUERY_API.md`                  | introducing a new timestamp index without breaking Part 1 history semantics      |
| `P3-04_MESH_STORE.md`                       | one graph authority, sensor reconciliation and synchronization                   |
| `P3-05_BFS_DESIGN.md`                       | deterministic fewest-hop traversal, cycles and unavailable forwarding            |
| `P3-06_PRIM_MST_DESIGN.md`                  | weighted MST, tie ordering, BFS/MST objective separation                         |
| `P3-06_REROUTING_SCENARIOS.md`              | version-aware recalculation and honest disconnection                             |
| `P3-08_FINAL_ACCESSIBILITY_VERIFICATION.md` | accessible graph/table interaction and responsive UI                             |
| `P3-09_CROSS_PILLAR_WORKFLOWS.md`           | keeping selected-device/history state coherent across modules                    |
| `P3-10_FINAL_PROACTIVE_VERIFICATION.md`     | separating rule-derived immediate risks from learned command recommendations     |
| `P3-11_FINAL_REGRESSION.md`                 | debugging cross-stack regression, deterministic focus, Docker/proxy verification |

P3-13 should convert these concrete engineering decisions into the required final report/PDF and attach final repository/CI evidence.

---

# 5. Technology recommendation handoff to P3-13

Recommendations must be justified against the **actual current constraints**:

- operational repositories are process-local;
- the API runs as one application instance for authoritative in-memory state;
- link mutations are a local simulation tool, not an authenticated network-management plane;
- SignalR is an invalidation mechanism rather than a durable event log;
- Docker Compose is a single-host deployment model;
- encrypted attachments use a named filesystem volume;
- load evidence is local and bounded, not a production-scale SLO.

P3-13 can therefore evaluate technologies for durable event ingestion, distributed persistence, managed secrets, metrics/tracing, container orchestration and long-term telemetry analytics **with an explicit migration rationale**. The final recommendation report should avoid suggesting services simply because they are fashionable.

---

# 6. Feedback incorporation

The only specific earlier assessment information supplied to the project ledger is the Part 2 **18/30** result for Predictive Action and Suggestion Engine. The detailed lecturer comment was not provided, so Smart-X does not invent one.

The final application responds conservatively by strengthening visible evidence:

- existing P2 learned action engine remains genuine and episode-based;
- P3-10 adds **separate** proactive topology/status risk insights from live graph state;
- provenance/freshness/affected IDs are shown instead of pretending a rule-derived network risk is ML output;
- authentic recent search evidence is bounded and visible;
- E2E proves first-visit topology failure insight, recovery and the separate learned-action journey.

See `docs/part3/FEEDBACK_UPDATES.md`. P3-13 must update the stale "pending" wording with final commit/CI references and close the ledger without claiming a retroactive mark change.

---

# 7. Main menu and architecture health

The unified client now exposes all three modules:

```text
/ingestion  -> Part 1
/realtime   -> Part 2
/topology   -> Part 3
```

P3-11 integrated browser regression verifies:

- one SPA document across all three pillars;
- retained selected server device;
- retained safe typed/search/history drafts;
- no persistence of file-input selections;
- direct URLs/reload/Back/Forward;
- keyboard activation and narrow layouts;
- topology read failure/retry without losing cross-pillar state;
- deterministic focus transfer to deep-linked topology workspace.

The complete final browser run recorded **99 passed, 1 intentionally skipped, 0 failed**.

---

# 8. Final verification references

- `docs/evidence/part3/p3-11-final-regression.json`
- `docs/part3/P3-11_FINAL_REGRESSION.md`
- `scripts/verify_part3_final_regression.py`
- `src/SmartX.Client/e2e/part3-p3-11-integrated-regression.spec.ts`

P3-11 final local gate also verified:

- audited .NET restore;
- .NET formatting;
- Release build;
- **1,106 backend tests passed**;
- frontend clean install/high-audit/lint/format/build;
- focused and complete Playwright regression;
- Docker Compose config/build/start;
- Nginx routes `/ingestion`, `/realtime`, `/topology`;
- topology API routes;
- `/hubs/realtime` negotiation advertising WebSockets;
- normal `docker compose down` preserving the attachment volume.

---

## P3-12 exit condition

P3-12 is complete when another developer/marker can use the root README and linked docs to:

1. restore/build/run Smart-X;
2. identify every active pillar;
3. locate the exact AVL/BFS/Prim source;
4. explain algorithm choice and complexity accurately;
5. reproduce history/routing/failure scenarios using real APIs/scripts;
6. find committed large-scale evidence without invented benchmark claims;
7. identify what remains for the formal P3-13 report/submission rather than confusing documentation completion with final submission completion.
