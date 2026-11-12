# P3-11 — AVL telemetry load and repeatable performance evidence

**Issue:** `[P3-11] Verify scalability, routing correctness, and full-system regression`
**Branch:** `test/part3-load-regression`
**Commit 1:** `test(avl): benchmark seeded timestamp ranges and index invariants`

> This document describes how to **generate evidence**. No p50/p95 figures or
> environment-specific execution results are supplied in source control until
> the corresponding real HTTP verification has actually run. The script must
> not be used against a production or internet-accessible API.

## 1. Correct architectural interpretation

The accepted telemetry path is `POST /api/sensors/{id}/telemetry/batches`.
`SensorTelemetryService` checks the sensor's registered `TelemetryDataType`
(`Float`, `Integer`, or `Boolean`) and passes accepted packets to the **existing**
`InMemoryTelemetryRepository`. The original `List<T>` is the authoritative
acceptance-order view, and the generic `AvlTelemetryIndex<T>` is its secondary,
timestamp-sorted index. Both views are updated under the per-series write lock.

The benchmark reads the **real** indexed API:

```text
GET /api/sensors/{id}/telemetry/range?fromUtc=...&toUtc=...&limit=1000
GET /api/sensors/{id}/telemetry/range?fromUtc=...&toUtc=...&limit=2
    &afterUtc=...&afterSequence=...
```

It does **not** benchmark the 100-row Part 1 recent-history endpoint as if
that endpoint were a historical range query. It **does** verify that
`GET /api/sensors/{id}/telemetry?limit=5` still returns the most recently
accepted packets in original ingestion order.

## 2. What the benchmark actually verifies

`scripts/verify_part3_telemetry_load.py` is dependency-free Python 3.10+ and:

1. Restricts the target API to loopback. Requires a **fresh**, process-local
   API with only the logical gateway and no links, and an unpaused worker.
2. Registers three genuine sensors, one per payload type, and obtains their
   server-issued UUIDs.
3. Sends the primary `Float` series in **descending capture-time order**,
   in sequential HTTP jagged batches of up to 250 readings. Additional smaller
   `Integer` and `Boolean` series prove native type fidelity.
4. Appends two additional accepted readings at a timestamp already in each
   series. Exact server batch receipts—not intended packet counts—establish
   successful acceptance. The active worker must fully drain.
5. Traverses **every accepted reading** using inclusive range queries and the
   `(afterUtc, afterSequence)` cursor. Each returned timestamp, typed value,
   UUID, and device identifier is checked against a deterministic mathematical
   oracle. The entire series is **not** accumulated in client memory.
6. Queries the exact repeated timestamp with `limit=2`, confirming all three
   values appear across two API pages in original acceptance order.
7. Confirms the legacy recent-history endpoint still returns the actual last
   five accepted packets in ingestion order, including the late duplicates.
8. Confirms reversed ranges, out-of-bounds limits, and partial cursors return
   HTTP 400 without damaging a subsequent valid indexed query.
9. Warms and alternately measures narrow **2-result** and broad **1,000-result
   first-page** GETs using 25 real requests each (configurable). The timings
   include network stack, .NET server, JSON output and client deserialization.
10. Writes a JSON summary **only after all checks pass**, never overwriting a
    prior evidence file. Failing/partial runs do not produce a passing report.

Default scenarios include an extra two repeated-time packets per series:

| Run | Primary Float base | Integer base | Boolean base | Expected accepted total |
| --- | ---: | ---: | ---: | ---: |
| 10k | 10,000 | 96 | 96 | 10,198 |
| 50k | 50,000 | 96 | 96 | 50,198 |

The expected totals are fixture calculations, **not measured results**. Only
successful HTTP 201 receipts, worker diagnostics, and complete AVL traversal
can establish the corresponding result for a particular run.

## 3. Start a dedicated local API

Run from the root of the repository. The bench must not share its backend with
Playwright, a live Vite instance that is generating traffic, the Docker demo,
or any external client. A new API process is essential for comparable runs
because Smart-X stores telemetry and topology in process-local memory.

Terminal A:

```bash
dotnet restore SmartX.sln
dotnet build SmartX.sln -c Release
ASPNETCORE_ENVIRONMENT=Development dotnet run \
  --project src/SmartX.Api --no-launch-profile \
  --urls http://127.0.0.1:5095
```

Terminal B (also at repository root):

```bash
python3 -m unittest discover -s scripts/tests \
  -p 'test_verify_part3_telemetry_load.py' -v

python3 scripts/verify_part3_telemetry_load.py \
  --base-url http://127.0.0.1:5095 \
  --count 10000 --typed-count 96 \
  --samples 25 --warmups 3 \
  --output docs/evidence/part3/telemetry-10k.json
```

Stop Terminal A with `Ctrl+C` and **start a fresh API process** using the same
command before the 50k run. Then run from Terminal B:

```bash
python3 scripts/verify_part3_telemetry_load.py \
  --base-url http://127.0.0.1:5095 \
  --count 50000 --typed-count 96 \
  --samples 25 --warmups 3 \
  --output docs/evidence/part3/telemetry-50k.json

python3 -m json.tool docs/evidence/part3/telemetry-10k.json > /dev/null
python3 -m json.tool docs/evidence/part3/telemetry-50k.json > /dev/null
```

If a run fails, capture the error, **restart the API**, fix the underlying
cause, and rerun. Existing JSON output names are never overwritten. Delete or
archive your *own* failed run's file only after inspecting it. The verifier
uses the actual `processedCount`, `acceptedCount`, and pending worker counters;
it does not assume that HTTP acceptance means real-time processing finished.

### Structural AVL tests (separate from HTTP measurement)

The API does not expose AVL node heights or rotation counts. These internal
invariants must instead be checked using the existing domain-level tests:

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~AvlTelemetryIndex'

dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~TelemetryRangeIndex'
```

See `tests/SmartX.Domain.Tests/Telemetry/AvlTelemetryIndexTests.cs`,
`AvlTelemetryIndexAdversarialTests.cs`,
`AvlTelemetryIndexPaginationTests.cs`, and
`AvlTelemetryIndexConcurrencyTests.cs` for structural balancing, equal-time
buckets, bounded cursors, and concurrent snapshot regression.

## 4. How measurements are calculated

- Timing uses Python `time.perf_counter_ns()` around each complete HTTP GET,
  including response parsing. It is **not** instrumented inside the AVL tree.
- `p50` is the median of the samples in milliseconds.
- `p95` uses the nearest-rank method: the sorted sample at index
  `ceil(0.95 * n) - 1` (zero-based).
- Warmups are excluded. Narrow and broad queries alternate to reduce ordering
  bias, though in-process cache state, OS scheduling and background services
  still affect results.
- Both scenarios query the **same populated Float sensor**. The narrow query
  returns two readings at one timestamp; the broad query returns one capped
  1,000-row page over the full interval. It does **not** retrieve all 10k/50k
  readings for every timed broad sample.
- Larger pages have different JSON serialization and transport costs. A p95
  difference is therefore **not** an isolated measure of AVL tree complexity.
- There is deliberately **no arbitrary CI latency cutoff**; CI tests should
  check correctness, while benchmark output provides factual context.

AVL lookup is expected to be `O(log n + k)` for a balanced index and `k`
reported readings, excluding JSON and transport. A successful run demonstrates
correct indexed retrieval at the selected sizes, but cannot by itself prove
that asymptotic bound. Measured values must always be attributed to the actual
machine and runtime configuration in each generated JSON file.

## 5. Evidence and limitations

Only committed artifacts that were generated by an actual successful run
should appear as `docs/evidence/part3/telemetry-10k.json` and
`telemetry-50k.json` (or equivalent descriptive names). Record the actual
command, date, machine architecture, result count, and p50/p95. Do not copy
example or fabricated numbers into the assessment report.

This remains a **simulation**. No physical device or RF network is tested.
Data is process-local and lost on API restart. Sequential cursor pages do not
promise cross-request transaction isolation under concurrent ingestion; the
benchmark enforces an isolated, drained workload before querying.

P3-11 Commit 2 will add larger registered mesh / BFS / Prim measurements;
Commit 3 will extend browser resilience regressions; Commit 4 will cover CI,
Docker/Compose and the final multi-pillar validation matrix.
