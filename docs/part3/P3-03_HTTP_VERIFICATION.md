# P3-03 — HTTP integration verification of AVL telemetry history

## Scope and trust boundary

P3-03 exposes the accepted Smart-X telemetry series through the existing
sensor-specific repository and its secondary AVL index. The `GET /range`
endpoint does **not** connect to a separate database or synthesize test data.
The new `TelemetryRangeEndpointTests` exercise the real HTTP request pipeline
using `WebApplicationFactory<Program>`, normal dependency injection, sensor
registration, and actual single/batch telemetry ingestion.

The repository remains process-local. Restarting the API loses its in-memory
history, and separate HTTP pages are live per-request snapshots rather than one
transaction-wide snapshot. Neither durability nor cross-page isolation against
concurrent inserts is claimed.

## Automated evidence

| Scenario | Reproducible assertion |
| --- | --- |
| Large real-API historical load | 3,072 accepted readings with distinct, scrambled timestamps; verify a 1,000-reading maximum page and an independently calculated nine-reading narrow window |
| Mixed, out-of-order historical batches | 241 accepted Integer readings in a four-part jagged batch, including an empty batch; independent timestamp/insertion-order reference comparison |
| Duplicate-time cursor boundaries | 53 readings with one identical timestamp, returned over eight pages, every value exactly once |
| Three native payload types | Float `0`, Integer `0`, Boolean `false`; correct nullable fields and registered sensor type |
| Empty/unknown sensor | Registered empty sensor returns `200` and empty page; unknown sensor returns `404` ProblemDetails |
| Sensor isolation | Two registered sensors with identical capture instants never leak telemetry into one another's responses |
| Inclusive UTC boundaries | Offset-equivalent timestamps at seven-digit fractional precision select the exact intended packet |
| Rejected batch | A malformed later jagged element produces `400` without partial writes to AVL or legacy history |
| Live reads | A later accepted packet appears on a subsequent query but does not mutate an earlier result |
| Concurrent HTTP ingestion | 32 simultaneous accepted readings are retained exactly once and agree with the existing recent-history order |
| Terminal cursor | Resuming after the final packet returns `200` with an empty terminal page |
| Invalid requests | Missing/malformed timestamps, non-UTC-compatible timestamps, reversed bounds, invalid limits and invalid composite cursors return `400` ProblemDetails |
| Part 1 backwards compatibility | The original `GET /telemetry?limit=100` keeps most-recent **ingestion order**, while `GET /range` uses UTC capture order |

These are **test definitions**, not a claim that the suite has passed in any
environment. Run the commands below and capture the actual runner summary.

## Local validation

From the repository root, on `feature/part3-range-query-api`:

```bash
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter FullyQualifiedName~TelemetryRangeEndpointTests
dotnet test SmartX.sln -c Release
dotnet format SmartX.sln --verify-no-changes
git diff --check
```

The existing domain AVL balancing tests, infrastructure repository tests and
Part 1/2 integration tests must still pass. Do not treat warnings emitted by
intentional failure-injection tests as evidence of a failed run; the test runner
exit code and final summaries are authoritative.

## Complexity explanation

For `n` distinct timestamp nodes and `k` entries in a page, an AVL tree has
height **O(log n)**. Finding a valid continuation key and enumerating a
bounded page uses an in-order traversal with subtree pruning, approximately
**O(log n + k)**, rather than sorting the complete ingestion history on each
request. Duplicate timestamp buckets preserve the original accepted order via
a zero-based sequence cursor.

This describes the algorithmic design; a separately measured performance
comparison and larger load evidence belong to P3-11.

## Pull-request evidence

After execution, retain the terminal summaries for the dedicated HTTP suite,
the full .NET Release suite and `dotnet format`. P3-03 is complete only once
all validations are green and Commit 3 is pushed to the feature branch.
