# Smart-X Part 3 — Evidence Index

This directory contains the **committed machine-readable evidence** generated for P3-11 and referenced by the P3-12 README/architecture documentation.

The evidence is designed to prove behaviour through the real Smart-X API and verification scripts. It is not a collection of manually typed benchmark claims.

[Root README](../../../README.md) · [Architecture/data structures](../../part3/ARCHITECTURE_AND_DATA_STRUCTURES.md) · [Rubric matrix](../../part3/ASSESSMENT_RUBRIC_MATRIX.md) · [Final regression design](../../part3/P3-11_FINAL_REGRESSION.md)

---

## Evidence files

| File                                                         | Result | What it proves                                                                                                                                          |
| ------------------------------------------------------------ | ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [`telemetry-10k.json`](telemetry-10k.json)                   | `pass` | Real API ingestion/indexing with a 10,000-reading primary Float fixture plus duplicate-time and typed Integer/Boolean checks.                           |
| [`telemetry-50k.json`](telemetry-50k.json)                   | `pass` | Same verification at a 50,000-reading primary Float fixture.                                                                                            |
| [`mesh-256.json`](mesh-256.json)                             | `PASS` | Real API 256-sensor graph, BFS, Prim/Kruskal cost check, island bridge, backup route, full gateway disconnection, restoration and invalid-write checks. |
| [`mesh-1000.json`](mesh-1000.json)                           | `PASS` | Same large-graph workflow at 1,000 sensors.                                                                                                             |
| [`p3-11-final-regression.json`](p3-11-final-regression.json) | `PASS` | Final audited restore/format/build/test/frontend/browser/Docker gate plus SHA-256 validation of the four load artifacts.                                |

The case difference between `pass` and `PASS` comes from the independently written telemetry/mesh evidence schemas. The final verifier treats those schemas separately and requires their expected success indicators.

---

# 1. AVL telemetry evidence

## 10k fixture

`telemetry-10k.json` records:

- requested primary history: **10,000** Float readings;
- indexed Float readings after duplicate-time additions: **10,002**;
- secondary Integer series: **98** indexed readings;
- secondary Boolean series: **98** indexed readings;
- total accepted across the three types: **10,198**;
- worker accepted delta = **10,198**;
- worker processed delta = **10,198**;
- descending-arrival verification;
- duplicate timestamp window with **3** retained Float values across 2 pages;
- composite cursor traversal verified exactly;
- invalid read requests rejected;
- original Part 1 recent ingestion order preserved.

Observed read timing samples (25 samples after 3 warmups):

| Query      | Rows returned/sample |          p50 |          p95 |
| ---------- | -------------------: | -----------: | -----------: |
| narrow     |                    2 | **0.648 ms** | **0.766 ms** |
| broad page |                1,000 | **1.725 ms** | **2.039 ms** |

## 50k fixture

`telemetry-50k.json` records:

- requested primary history: **50,000** Float readings;
- indexed Float readings after duplicate-time additions: **50,002**;
- secondary Integer series: **98**;
- secondary Boolean series: **98**;
- total accepted across types: **50,198**;
- worker accepted delta = **50,198**;
- worker processed delta = **50,198**;
- the same duplicate/cursor/validation/legacy-history checks as the 10k run.

Observed read timing samples:

| Query      | Rows returned/sample |          p50 |          p95 |
| ---------- | -------------------: | -----------: | -----------: |
| narrow     |                    2 | **0.521 ms** | **0.754 ms** |
| broad page |                1,000 | **1.625 ms** | **1.803 ms** |

### Timing caveat

These are **local client-observed HTTP end-to-end measurements**. They include HTTP, JSON, server and networking overhead. A broad timing sample is one 1,000-row page, not complete retrieval of all 10k/50k readings. They are not hard CI thresholds and are not an asymptotic-complexity proof.

AVL structural invariants are verified separately by Domain tests because internal nodes are intentionally not exposed over HTTP.

### Reproduce

Read the verifier help before running it because comparable evidence expects a freshly restarted local API and a quiet process-local state:

```bash
python3 scripts/verify_part3_telemetry_load.py --help
```

Methodology and claim boundaries:

- [`../../part3/LOAD_VERIFICATION.md`](../../part3/LOAD_VERIFICATION.md)
- [`../../part3/P3-09_AVL_HISTORY_DESIGN.md`](../../part3/P3-09_AVL_HISTORY_DESIGN.md)

---

# 2. Mesh graph / BFS / Prim evidence

## 256-sensor fixture

`mesh-256.json` begins with:

- **256** registered sensors + one gateway vertex;
- **231** reachable sensors;
- one genuine disconnected component containing **25** sensors;
- initial Prim total simulated cost **231**;
- independent Kruskal total **231**.

Then:

1. a cost-5 bridge joins the island;
2. all **256** sensors become reachable;
3. Prim and independent Kruskal both produce total **260**;
4. the primary gateway edge is removed;
5. all sensors remain reachable through the configured backup; selected route moves from 1 hop to 2 hops and tree total becomes **267**;
6. all gateway links are removed; **256/256** sensors are reported disconnected and the gateway-rooted tree has zero selected edges;
7. the primary path is restored; all **256** become reachable and cost returns to **260**.

Observed read timings (11 samples):

| Read                  |       Median |          p95 |
| --------------------- | -----------: | -----------: |
| topology              | **1.559 ms** | **1.816 ms** |
| BFS reachability      | **1.587 ms** | **1.803 ms** |
| selected device route | **2.049 ms** | **5.512 ms** |
| Prim MST              | **2.125 ms** | **7.261 ms** |

## 1,000-sensor fixture

`mesh-1000.json` begins with:

- **1,000** registered sensors + gateway;
- **900** reachable sensors;
- one disconnected component containing **100** sensors;
- initial Prim/Kruskal total **900**.

After the bridge:

- **1,000** reachable;
- Prim/Kruskal total **1004**.

After primary gateway removal:

- all **1,000** remain reachable through the backup;
- selected route moves to 2 hops;
- Prim/Kruskal total **1011**.

After every gateway link is removed:

- **1,000/1,000** sensors are disconnected;
- the gateway-rooted tree contains zero selected edges.

After restoration:

- all **1,000** are reachable again;
- total returns to **1004**.

Observed read timings (11 samples):

| Read                  |       Median |           p95 |
| --------------------- | -----------: | ------------: |
| topology              | **5.118 ms** |  **6.422 ms** |
| BFS reachability      | **3.409 ms** |  **5.490 ms** |
| selected device route | **6.202 ms** | **10.003 ms** |
| Prim MST              | **6.407 ms** | **17.734 ms** |

### Invalid-write checks

Both mesh evidence files verify:

- duplicate link request -> HTTP **400**;
- invalid link cost -> HTTP **400**;
- empty sensor route -> HTTP **400**;
- unknown sensor route -> HTTP **404**;
- graph version unchanged after rejected writes;
- link count unchanged after rejected writes.

### Reproduce

```bash
python3 scripts/verify_part3_mesh_load.py --help
```

Methodology:

- [`../../part3/MESH_LOAD_VERIFICATION.md`](../../part3/MESH_LOAD_VERIFICATION.md)
- [`../../part3/P3-05_BFS_DESIGN.md`](../../part3/P3-05_BFS_DESIGN.md)
- [`../../part3/P3-06_PRIM_MST_DESIGN.md`](../../part3/P3-06_PRIM_MST_DESIGN.md)

As with the telemetry evidence, local request timings are observations, not production SLOs.

---

# 3. Final P3-11 regression evidence

`p3-11-final-regression.json` first validates the four load artifacts and records a SHA-256 fingerprint for each one. It then runs the final release gate.

The committed run records:

- audited .NET restore — **PASS**;
- .NET formatting — **PASS**;
- Release build — **PASS**, 0 warnings / 0 errors;
- backend regression — **PASS**;
- backend test totals — **249 + 313 + 230 + 314 = 1,106 passed**, 0 failed;
- frontend clean install — **PASS**;
- high-severity npm audit gate — **PASS**;
- ESLint — **PASS**;
- Prettier — **PASS**;
- production client build — **PASS**;
- focused P3-11 Playwright — **3 passed**;
- complete Playwright — **99 passed, 1 intentionally skipped, 0 failed**;
- Docker Compose config — **PASS**;
- Docker image build — **PASS**;
- Docker start/health — **PASS**.

The running production-style Nginx boundary returned HTTP 200 for:

- `/healthz`
- `/api/health`
- `/ingestion`
- `/realtime`
- `/topology`
- `/api/topology`
- `/api/topology/reachability`
- `/api/topology/mst`

`/hubs/realtime/negotiate?negotiateVersion=1` also returned 200 and advertised WebSockets.

Shutdown used normal:

```bash
docker compose down
```

The verifier does **not** delete the named attachment volume.

### Reproduce the final gate

```bash
python3 scripts/verify_part3_final_regression.py --with-docker
```

The final verifier is intentionally strict: if one required load artifact is missing/invalid, or any backend/frontend/browser/Docker gate fails, it does not write a successful final evidence result.

---

# 4. Evidence interpretation rules

When using these files in the final report/video:

- quote the generated values exactly rather than rounding into new claims;
- call the timings **observed local end-to-end request timings**, not isolated algorithm timings;
- explain complexity from the algorithm/source, not by extrapolating from two timing points;
- call link weights **simulated routing/energy costs**, not measured RF/power;
- distinguish BFS fewest-hop routes from Prim minimum-total-tree cost;
- describe complete disconnection as a valid result, not a failed self-healing feature;
- do not imply the opt-in large browser load test ran as part of every ordinary Playwright invocation;
- do not claim the earlier Part 2 18/30 prediction mark changed unless an actual reassessment says so.

These rules keep the evidence strong without overstating what the fixtures prove.
