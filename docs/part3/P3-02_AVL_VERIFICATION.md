# P3-02 - AVL telemetry index verification

**Implementation scope:** custom, generic AVL structure under `SmartX.Domain.Telemetry`.
**Current status:** Domain-only index and tests. P3-03 will integrate it with accepted telemetry persistence and a real API endpoint; do **not** claim that this document proves API integration.

## Purpose and design

`AvlTelemetryIndex<T>` is a self-balancing binary search tree keyed by canonical UTC capture timestamps. Each node maintains its subtree height and an insertion-ordered list of packets with the same timestamp. LL/RR/LR/RL rotations prevent sorted input from creating a linear chain. A per-index private lock serialises structural changes and the bounded copying of range results.

The index returns its original immutable `TelemetryPacket<T>` objects in a newly allocated read-only list. Later inserts cannot change an earlier snapshot. A timestamp-bucket insertion does not overwrite a packet or create an extra tree node.

- **Insert:** `O(log u)` worst-case AVL navigation and rotation, plus amortised `O(1)` bucket append, where `u` is the number of distinct timestamps.
- **Inclusive range query:** `O(log u + k)` for a balanced tree and `k` returned packets, with branches outside the interval pruned. A query stops at its result limit. Snapshot allocation and serialization are separate costs.
- **Space:** `O(u + p)` for `u` timestamp nodes and `p` packet references across their buckets. Values remain strongly typed; this index is in memory only.

The default query limit is 100 packets; the maximum is 10,000. These are result-size bounds, **not** pagination. If a large same-timestamp bucket crosses the limit, P3-03 will need an appropriate cursor/offset strategy to support complete client retrieval without losing entries.

## Automated coverage

| Test class | Purpose |
| --- | --- |
| `AvlTelemetryIndexTests` | Four rotations, counts, UTC-normalised duplicates, inclusive bounds, stable buckets, read-only snapshots, invalid inputs, typed payloads |
| `AvlTelemetryIndexAdversarialTests` | 8,192 increasing/decreasing keys; seeded permutations; 8,000-item independent reference comparison; tick accuracy; 6,000 same-time packets; maximum result limits |
| `AvlTelemetryIndexConcurrencyTests` | Concurrent distinct-key insertion; same-time collisions with per-writer order; simultaneous readers/writers; immutable older snapshots; typed-series isolation; deterministic 27,000-packet fixture |

### Reproduce on the feature branch

From the repository root:

```bash
git branch --show-current
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter FullyQualifiedName~AvlTelemetryIndex
dotnet test SmartX.sln -c Release
dotnet format SmartX.sln --verify-no-changes
git diff --check
```

Record actual results in the PR description. Do not copy earlier test counts as evidence of a new run.

## High-volume fixture and limitations

The final concurrency test seeds **25,000 unique timestamps plus 2,000 duplicate-time packets** in a reproducible non-sorted order and verifies a narrow 50-reading query, a two-packet collision bucket, counts, height, and full AVL invariants. This is functional scaling evidence, not a timing benchmark. It deliberately does **not** assert millisecond thresholds, which would be flaky across laptops and CI runners.

The concurrent tests protect consistent snapshots while the index is mutated. They do not claim lock-free writes or linear speedup: per-index insertion and reading are serialized for correctness. P3-03 must keep the index and the authoritative append-order repository consistent under one write boundary, while P3-11 should evaluate higher-scale latency using separate repeatable measurements.

## Commit and issue handoff

- **P3-02:** Complete when all three AVL test classes pass, the full backend suite is green, and the PR is merged into `dev`.
- **P3-03:** Wire the same accepted typed packets to the current telemetry repository and its secondary AVL index, then provide real timestamp-filtered HTTP queries.