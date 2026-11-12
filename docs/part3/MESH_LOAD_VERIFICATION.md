# Smart-X Part 3 — Large Mesh BFS, MST and Route-Repair Verification

## Purpose

P3-11 Commit 2 provides reproducible scalability evidence for the **actual Smart-X HTTP API** and the final-PoE mesh data structures. It does not add a second graph implementation to production code and it does not replace the existing C# unit/integration tests.

The verification harness creates deterministic simulated IoT meshes at **256** and **1,000 registered sensor nodes**, then checks:

- the real adjacency-list topology returned by `GET /api/topology`;
- gateway reachability and **minimum-hop BFS** results from `GET /api/topology/reachability`;
- the production **Prim minimum spanning tree** from `GET /api/topology/mst`;
- an **independent Kruskal calculation** performed only inside the verification script;
- selected-device BFS and MST routes from `GET /api/topology/devices/{id}/route`;
- a genuine disconnected component that becomes reachable through an explicit bridge;
- primary-gateway-link removal, backup rerouting, complete disconnection and restoration;
- invalid duplicate/cost/route requests without graph mutation; and
- observed HTTP query p50/p95 timings for topology, BFS, MST and selected-device route reads.

The PoE requires graph traversal and a minimum spanning tree to model mesh routing efficiently. This evidence makes those behaviours visible at a scale far larger than the small illustrative UI topology while still using the same authoritative API and in-memory graph implementation.

## Deterministic fixtures

Each run starts from a **fresh API process** because Smart-X sensor and topology stores are intentionally process-local.

| Evidence run | Initially reachable sensors | Disconnected island | Expected initial MST cost | Expected bridged MST cost |
| --- | ---: | ---: | ---: | ---: |
| 256 sensors | 231 | 25 | 231 | 260 |
| 1,000 sensors | 900 | 100 | 900 | 1,004 |

Construction rules:

1. A cost-1 gateway edge and cost-1 sensor chain form the initial reachable component.
2. A separate cost-1 chain forms the disconnected island.
3. An expensive gateway backup edge and two expensive redundant edges create real cycles without changing the intended initial MST.
4. A cost-5 bridge joins the island to the reachable component.
5. The primary gateway edge is removed to force the expensive backup route.
6. The backup edge is then removed to prove complete gateway disconnection.
7. A new primary gateway edge restores full reachability.

The initial gateway-rooted tree therefore contains one selected cost-1 edge for every initially reachable sensor. Once bridged, the tree includes the island's internal edges plus the cost-5 bridge.

## Independent algorithm checks

The production Smart-X MST uses **Prim's algorithm**. The Python harness does not duplicate that implementation. Instead, it independently:

- rebuilds an adjacency list from the public topology snapshot;
- runs BFS from the logical `gateway` and compares **every reachable hop count** with the API response; and
- runs **Kruskal's algorithm with union-find** over only the gateway-reachable eligible component and compares the independent total/edge count with the production Prim result.

Using a second MST algorithm is deliberate assessment evidence: agreement is stronger than merely asserting that the API returned a number.

## Prerequisites

From the repository root:

```bash
git switch test/part3-load-regression
git status --short
dotnet build SmartX.sln -c Release
python3 scripts/tests/test_verify_part3_mesh_load.py
python3 -m py_compile scripts/verify_part3_mesh_load.py scripts/tests/test_verify_part3_mesh_load.py
```

The harness uses only the Python standard library.

## 256-node evidence run

Start a **fresh** API process in Terminal A:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5095 \
dotnet run \
  --project src/SmartX.Api/SmartX.Api.csproj \
  -c Release \
  --no-build \
  --no-launch-profile
```

In Terminal B:

```bash
python3 scripts/verify_part3_mesh_load.py \
  --base-url http://127.0.0.1:5095 \
  --sensors 256 \
  --samples 11 \
  --output docs/evidence/part3/mesh-256.json
```

Expected correctness summary:

```text
initial: reachable=231, disconnected=25, MST=231
bridged: reachable=256, disconnected=0, MST=260
```

Stop the API with `Ctrl+C` before the 1,000-node run.

## 1,000-node evidence run

Start a **new/fresh** API process using the same Terminal A command. Then run:

```bash
python3 scripts/verify_part3_mesh_load.py \
  --base-url http://127.0.0.1:5095 \
  --sensors 1000 \
  --samples 11 \
  --output docs/evidence/part3/mesh-1000.json
```

Expected correctness summary:

```text
initial: reachable=900, disconnected=100, MST=900
bridged: reachable=1000, disconnected=0, MST=1004
```

Stop the API after the evidence file is written.

## Evidence JSON semantics

Each generated JSON file records:

- the fixture size and expected mathematical totals;
- initial disconnected-island BFS/MST state;
- post-bridge full connectivity;
- backup route after removing the primary gateway edge;
- full disconnection after removing both gateway edges;
- restored connectivity after recreating the primary edge;
- independent Kruskal totals and edge counts;
- invalid-request safety checks;
- observed read-only p50/p95 timing summaries; and
- local platform/Python metadata.

The evidence intentionally stores compact aggregate projections instead of copying thousands of topology node/link rows into Git. Correctness is derived from the complete real API responses in memory before the compact evidence file is written.

## Timing interpretation

The recorded timings are **observations, not pass/fail service-level objectives**. Setup requests are excluded, there is one unmeasured warm-up request per read endpoint, and no wall-clock threshold is enforced in CI. The measurements are useful for demonstrating that the algorithms remain practical under the seeded assessment workload without creating a machine-dependent grading condition.

## Final regression after both runs

```bash
python3 scripts/tests/test_verify_part3_mesh_load.py
python3 -m py_compile scripts/verify_part3_mesh_load.py scripts/tests/test_verify_part3_mesh_load.py

dotnet format SmartX.sln --verify-no-changes --no-restore
dotnet test SmartX.sln -c Release --no-restore

git diff --check
```

The expected backend regression baseline immediately before this commit is **1,106 passing tests**. Record the actual result from the current branch rather than editing evidence to force that number if the suite legitimately changes later.

## Commit scope

Tracked files for P3-11 Commit 2:

```text
scripts/verify_part3_mesh_load.py
scripts/tests/test_verify_part3_mesh_load.py
docs/part3/MESH_LOAD_VERIFICATION.md
docs/evidence/part3/mesh-256.json
docs/evidence/part3/mesh-1000.json
```

The two JSON files must be generated by the real runs above. Do **not** commit fabricated or hand-edited benchmark evidence.

Recommended commit message:

```text
test(mesh): stress large graph traversal MST and route repair
```
