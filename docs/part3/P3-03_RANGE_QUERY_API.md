# P3-03 - AVL-backed historical telemetry API

## Purpose

`GET /api/sensors/{sensorProfileId}/telemetry/range` reads accepted telemetry from
Smart-X's existing, **process-local**, per-sensor/per-payload-type AVL index.
It is read-only and uses the same packets accepted by the Part 1 ingestion
endpoints. The prior `GET /api/sensors/{id}/telemetry` endpoint still returns
most-recent packets in **ingestion order**; its contract has not changed.

## Request parameters

| Parameter | Required | Rule |
| --- | --- | --- |
| `fromUtc` | Yes | Inclusive ISO-8601 capture timestamp, with `Z` or explicit offset |
| `toUtc` | Yes | Inclusive timestamp; UTC instant must be at or after `fromUtc` |
| `limit` | No | Default 100, minimum 1, maximum 1,000 |
| `afterUtc` | Only when paging | Cursor timestamp returned as `nextCursor.capturedAtUtc` |
| `afterSequence` | Only when paging | Cursor sequence returned as `nextCursor.sequence` (zero-based) |

The two continuation fields **must be supplied together**. Keep the same
sensor, `fromUtc`, and `toUtc` on every page. A timestamp alone is insufficient:
readings captured at the same time are stored in a stable insertion-order
bucket. The sequence selects an exact position in that bucket.

For example, substitute a registered sensor ID:

```http
GET /api/sensors/{sensorProfileId}/telemetry/range?fromUtc=2026-11-10T07:00:00Z&toUtc=2026-11-10T11:00:00Z&limit=100
```

The **response schema** is `TelemetryRangePageResponse`:

```json
{
  "sensorProfileId": "<registered-sensor-guid>",
  "telemetryDataType": "Integer",
  "fromUtc": "2026-11-10T07:00:00+00:00",
  "toUtc": "2026-11-10T11:00:00+00:00",
  "limit": 100,
  "readings": [],
  "hasMore": false,
  "nextCursor": null
}
```

This is an **illustrative schema example**, not captured runtime evidence.
When `hasMore` is true, `nextCursor` has this shape:

```json
{
  "capturedAtUtc": "2026-11-10T08:30:00+00:00",
  "sequence": 3
}
```

Supply those values as `afterUtc` and `afterSequence` on the next request.
URL-encode `+` in timezone offsets as `%2B` when constructing a URL by hand.

## Ordering and algorithms

- Results are sorted by **UTC capture time**, ascending.
- Readings with the same timestamp retain **acceptance/insertion order**.
- The AVL prunes branches outside the inclusive range and below the cursor.
- Each page reads at most `limit + 1` indexed entries to determine `hasMore`.
- Finding a valid cursor and traversing its page costs approximately
  **O(log n + k)** for *n* distinct timestamps and *k* returned entries,
  excluding response serialisation and packet mapping.
- A returned page is a read-only snapshot. **Separate HTTP requests observe
  the live index**. New telemetry may appear between pages; no global
  cross-request snapshot or durability across API restarts is claimed.

## Error responses

- `400 application/problem+json`: omitted, malformed, non-offset or reversed
  timestamps; invalid limit; incomplete or unrecognised continuation.
- `404 application/problem+json`: sensor is not registered.
- `200 application/json`: the sensor exists, even if the time range contains
  no readings (`readings: []`, `hasMore: false`).

The existing `ApiExceptionHandler` supplies ProblemDetails for application
validation and missing sensor exceptions; no separate controller-local error
format is introduced.

## Verification

Commit 2 adds AVL cursor and repository paging tests. Commit 3 will provide
full HTTP integration coverage, including all three native telemetry types,
large jagged batches, duplicate timestamps across page boundaries, invalid
requests, and compatibility with the prior recent-history endpoint.
