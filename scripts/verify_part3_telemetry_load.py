#!/usr/bin/env python3
"""P3-11: verify and measure the REAL Smart-X AVL telemetry range API.

This is an explicit, local-only, destructive-to-TEST-STATE benchmark: it creates
three registered sensors and submits thousands of genuine accepted packets.
It never touches physical devices, actuator commands, or a remote server.

Python standard library only (3.10+). Run against a fresh DEVELOPMENT API:

    python3 scripts/verify_part3_telemetry_load.py --count 10000 \
        --output docs/evidence/part3/telemetry-10k.json

Restart the API before running the 50k scenario. Results are recorded only if
all acceptance, cursor, type, legacy-history and worker checks pass. End-to-end
HTTP timings include networking, serialization and JSON parsing; they are NOT
isolated AVL operation timings or a proof of asymptotic complexity.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import platform
import statistics
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen
from uuid import uuid4


UTC = timezone.utc
FIRST_CAPTURE_UTC = datetime(2026, 10, 1, tzinfo=UTC)
ALLOWED_KINDS = ("Float", "Integer", "Boolean")
MAX_API_RANGE_PAGE = 1_000
MAX_HTTP_RESPONSE = 4 * 1024 * 1024


class VerificationError(RuntimeError):
    """Raised whenever a measured scenario fails a real correctness check."""


def require(condition: bool, message: str) -> None:
    """Keep correctness checks active even when Python assertions are disabled."""
    if not condition:
        raise VerificationError(message)


def timestamp_at(origin: datetime, ordinal: int) -> str:
    """Produce unambiguous UTC timestamps for independently reproducible data."""
    return (origin + timedelta(seconds=ordinal)).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def parse_utc(value: Any) -> datetime:
    """Parse API timestamps exactly, including .NET's +00:00 representation."""
    require(isinstance(value, str), "Timestamp must be an ISO 8601 string")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise VerificationError(f"Invalid API timestamp: {value!r}") from error
    require(parsed.tzinfo is not None, "API timestamp must include a UTC offset")
    return parsed.astimezone(UTC)


def value_for(kind: str, ordinal: int) -> float | int | bool:
    """Generate exact, type-specific values (including negative ints / false)."""
    if kind == "Float":
        # Multiples of 1/8 have exact IEEE-754 binary representations.
        return 12.0625 + (ordinal % 241) / 8
    if kind == "Integer":
        return (ordinal % 101) - 50
    if kind == "Boolean":
        return ordinal % 2 == 0
    raise VerificationError(f"Unsupported telemetry type: {kind}")


def duplicates_for(kind: str) -> tuple[float | int | bool, float | int | bool]:
    """Two distinguishable accepted values at an existing capture instant."""
    if kind == "Float":
        return (33.125, 33.375)
    if kind == "Integer":
        return (-701, -702)
    if kind == "Boolean":
        return (False, True)
    raise VerificationError(f"Unsupported telemetry type: {kind}")


def packet(kind: str, at_utc: str, value: float | int | bool) -> dict[str, Any]:
    """Preserve JSON null vs zero/false and never submit a mismatched field."""
    require(kind in ALLOWED_KINDS, "Unknown telemetry type")
    return {
        "capturedAtUtc": at_utc,
        "floatValue": value if kind == "Float" else None,
        "integerValue": value if kind == "Integer" else None,
        "booleanValue": value if kind == "Boolean" else None,
    }


def valid_local_base(base_url: str) -> str:
    """Refuse non-loopback API targets: this harness writes a large test load."""
    parsed = urlsplit(base_url)
    require(parsed.scheme in ("http", "https"), "API URL must start with http(s)://")
    require(
        parsed.hostname in ("127.0.0.1", "localhost", "::1"),
        "Refusing a non-loopback API target; run against local Development only",
    )
    require(not parsed.username and not parsed.password, "Credentials in URL are prohibited")
    require(not parsed.query and not parsed.fragment, "API URL may not have query/fragment")
    require(parsed.path in ("", "/"), "API URL may not include a path")
    return base_url.rstrip("/")


class LocalApi:
    """Small, strict HTTP client; never silently retries writes or drops errors."""

    def __init__(self, base_url: str, timeout_seconds: float) -> None:
        self.base_url = valid_local_base(base_url)
        self.timeout_seconds = timeout_seconds

    def request(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        expected_status: int = 200,
        return_array: bool = False,
    ) -> dict[str, Any] | list[Any]:
        require(path.startswith("/"), "HTTP path must be root relative")
        url = self.base_url + path
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8") if payload is not None else None
        headers = {"Accept": "application/json", "Cache-Control": "no-store"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        req = Request(url, data=body, headers=headers, method=method)
        try:
            with urlopen(req, timeout=self.timeout_seconds) as response:
                status, raw = response.status, response.read(MAX_HTTP_RESPONSE + 1)
        except HTTPError as error:
            status, raw = error.code, error.read(MAX_HTTP_RESPONSE + 1)
        except (URLError, TimeoutError, OSError) as error:
            raise VerificationError(f"{method} {path}: API request failed: {error}") from error
        require(len(raw) <= MAX_HTTP_RESPONSE, f"{method} {path}: response too large")
        require(status == expected_status, f"{method} {path}: HTTP {status}, expected {expected_status}; {raw[:400]!r}")
        try:
            result = json.loads(raw)
        except (ValueError, UnicodeError) as error:
            raise VerificationError(f"{method} {path}: response was not valid JSON") from error
        expected_type = list if return_array else dict
        require(isinstance(result, expected_type), f"{method} {path}: expected JSON {expected_type.__name__}")
        return result

    def get(self, path: str, expected_status: int = 200) -> dict[str, Any]:
        result = self.request("GET", path, expected_status=expected_status)
        require(isinstance(result, dict), "GET response type mismatch")
        return result

    def get_array(self, path: str) -> list[Any]:
        """Part 1's legacy recent-history endpoint returns a JSON array."""
        result = self.request("GET", path, return_array=True)
        require(isinstance(result, list), "GET recent response type mismatch")
        return result

    def post(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        result = self.request("POST", path, payload, expected_status=201)
        require(isinstance(result, dict), "POST response type mismatch")
        return result


def range_path(
    sensor_id: str,
    from_utc: str,
    to_utc: str,
    limit: int,
    cursor: dict[str, Any] | None = None,
) -> str:
    """Pass exactly the server's two-part keyset continuation, if provided."""
    query: dict[str, str] = {
        "fromUtc": from_utc,
        "toUtc": to_utc,
        "limit": str(limit),
    }
    if cursor is not None:
        require(isinstance(cursor.get("capturedAtUtc"), str), "Cursor missing timestamp")
        sequence = cursor.get("sequence")
        require(type(sequence) is int and sequence >= 0, "Cursor missing nonnegative sequence")
        query["afterUtc"] = cursor["capturedAtUtc"]
        query["afterSequence"] = str(sequence)
    return f"/api/sensors/{sensor_id}/telemetry/range?{urlencode(query)}"


@dataclass(frozen=True)
class Series:
    """Compact oracle: full expected output is computed, never loaded in RAM."""

    sensor_id: str
    identifier: str
    kind: str
    count: int
    origin: datetime

    @property
    def target(self) -> int:
        return self.count // 2

    @property
    def accepted_count(self) -> int:
        return self.count + 2

    def expected_row(self, position: int) -> tuple[str, float | int | bool]:
        """Describe the sorted AVL result, including same-time insertion order."""
        require(0 <= position < self.accepted_count, "Expected position out of range")
        if position == self.target + 1:
            return timestamp_at(self.origin, self.target), duplicates_for(self.kind)[0]
        if position == self.target + 2:
            return timestamp_at(self.origin, self.target), duplicates_for(self.kind)[1]
        ordinal = position if position <= self.target else position - 2
        return timestamp_at(self.origin, ordinal), value_for(self.kind, ordinal)


def validate_value(
    reading: Any, series: Series, expected_time: str,
    expected_value: float | int | bool,
) -> None:
    """Verify full typed DTO, sensor scope, timestamp and exact payload value."""
    require(isinstance(reading, dict), "Reading was not an object")
    require(isinstance(reading.get("sensorProfileId"), str), "Missing reading sensor ID")
    require(reading["sensorProfileId"].lower() == series.sensor_id.lower(), "Wrong sensor in result")
    require(reading.get("deviceIdentifier") == series.identifier, "Wrong sensor identifier")
    require(reading.get("telemetryDataType") == series.kind, "Wrong telemetry payload type")
    require(parse_utc(reading.get("capturedAtUtc")) == parse_utc(expected_time), "Wrong capture timestamp")
    key = {"Float": "floatValue", "Integer": "integerValue", "Boolean": "booleanValue"}[series.kind]
    actual = reading.get(key)
    # JSON can serialize integral doubles as either `13` or `13.0`, while the
    # registered telemetryDataType and populated field still establish Float.
    if series.kind == "Float":
        require(type(actual) in (int, float) and float(actual) == expected_value,
                f"Incorrect Float value: {actual!r} != {expected_value!r}")
    else:
        require(type(actual) is type(expected_value) and actual == expected_value,
                f"Incorrect typed value: {actual!r} != {expected_value!r}")
    for other in ("floatValue", "integerValue", "booleanValue"):
        if other != key:
            require(reading.get(other) is None, "Unexpected extra populated telemetry value")


def validate_reading(reading: Any, series: Series, position: int) -> None:
    """Validate one chronologically expected row in the AVL traversal."""
    expected_time, expected_value = series.expected_row(position)
    validate_value(reading, series, expected_time, expected_value)


def verify_full_traversal(api: Any, series: Series, page_size: int) -> dict[str, Any]:
    """Traverse EVERY indexed row with O(page_size) response memory.

    The acceptance receipts supply the target count. The API has no totalCount,
    so we independently verify each position against a deterministic oracle.
    """
    first = timestamp_at(series.origin, 0)
    last = timestamp_at(series.origin, series.count - 1)
    previous_cursor: tuple[datetime, int] | None = None
    cursor: dict[str, Any] | None = None
    checked = 0
    pages = 0
    max_pages = math.ceil(series.accepted_count / page_size) + 1
    while True:
        pages += 1
        require(pages <= max_pages, "Cursor traversal did not terminate")
        result = api.get(range_path(series.sensor_id, first, last, page_size, cursor))
        rows = result.get("readings")
        require(isinstance(rows, list) and 0 < len(rows) <= page_size, "Invalid AVL range page length")
        require(isinstance(result.get("sensorProfileId"), str), "Range page omitted sensor UUID")
        require(result["sensorProfileId"].lower() == series.sensor_id.lower(), "Range page sensor mismatch")
        require(result.get("telemetryDataType") == series.kind, "Range page telemetry type mismatch")
        for reading in rows:
            require(checked < series.accepted_count, "AVL returned extra readings")
            validate_reading(reading, series, checked)
            checked += 1
        has_more = result.get("hasMore")
        require(type(has_more) is bool, "Range page hasMore must be Boolean")
        next_cursor = result.get("nextCursor")
        if not has_more:
            require(next_cursor is None, "Terminal page unexpectedly contained a cursor")
            break
        require(isinstance(next_cursor, dict), "Nonterminal page missing cursor")
        next_time = parse_utc(next_cursor.get("capturedAtUtc"))
        next_sequence = next_cursor.get("sequence")
        require(type(next_sequence) is int and next_sequence >= 0, "Invalid continuation sequence")
        pair = (next_time, next_sequence)
        require(previous_cursor is None or pair > previous_cursor, "AVL continuation did not advance")
        previous_cursor = pair
        cursor = next_cursor
    require(checked == series.accepted_count, f"Indexed rows {checked} != accepted {series.accepted_count}")
    return {"indexedReadings": checked, "pages": pages, "pageLimit": page_size}


def verify_narrow(api: Any, series: Series) -> dict[str, Any]:
    """Prove the composite cursor retains all three equal-time packets."""
    instant = timestamp_at(series.origin, series.target)
    first = api.get(range_path(series.sensor_id, instant, instant, 2))
    rows = first.get("readings")
    require(isinstance(rows, list) and len(rows) == 2, "Narrow first page must contain two readings")
    validate_reading(rows[0], series, series.target)
    validate_reading(rows[1], series, series.target + 1)
    require(first.get("hasMore") is True, "Narrow duplicate window should have a next page")
    require(isinstance(first.get("nextCursor"), dict), "Narrow first page missing cursor")
    second = api.get(range_path(series.sensor_id, instant, instant, 2, first["nextCursor"]))
    tail = second.get("readings")
    require(isinstance(tail, list) and len(tail) == 1, "Narrow continuation lost or duplicated a packet")
    validate_reading(tail[0], series, series.target + 2)
    require(second.get("hasMore") is False and second.get("nextCursor") is None, "Narrow cursor should end")
    return {
        "timestampUtc": instant,
        "expectedRows": 3,
        "verifiedRows": len(rows) + len(tail),
        "pageLimit": 2,
        "pageCount": 2,
        "typedValues": [row[{"Float": "floatValue", "Integer": "integerValue", "Boolean": "booleanValue"}[series.kind]] for row in [*rows, *tail]],
    }


def verify_recent_history(api: Any, series: Series) -> dict[str, Any]:
    """Protect the original Part 1 ingestion order, distinct from AVL order.

    The three last ordinary inserts are timestamps 2, 1, 0; the two trailing
    same-time inserts come afterwards. This MUST NOT be sorted on the server.
    """
    readings = api.get_array(f"/api/sensors/{series.sensor_id}/telemetry?limit=5")
    require(len(readings) == 5, "Legacy recent history did not contain the last five accepted packets")
    expectations = [
        (timestamp_at(series.origin, ordinal), value_for(series.kind, ordinal))
        for ordinal in (2, 1, 0)
    ] + [
        (timestamp_at(series.origin, series.target), value)
        for value in duplicates_for(series.kind)
    ]
    for reading, (expected_time, expected_value) in zip(readings, expectations, strict=True):
        validate_value(reading, series, expected_time, expected_value)
    return {"verifiedRecentCount": 5, "originalIngestionOrderPreserved": True}


def percentile_summary(samples_ms: list[float]) -> dict[str, float | int]:
    """Use exact median and nearest-rank 95th percentile; do not interpolate."""
    require(len(samples_ms) >= 5, "At least five timing samples are required")
    require(all(math.isfinite(value) and value >= 0 for value in samples_ms), "Invalid elapsed timing")
    ordered = sorted(samples_ms)
    p95 = ordered[math.ceil(0.95 * len(ordered)) - 1]
    return {
        "samples": len(ordered),
        "p50Ms": round(statistics.median(ordered), 3),
        "p95Ms": round(p95, 3),
        "minMs": round(ordered[0], 3),
        "maxMs": round(ordered[-1], 3),
    }


def benchmark_ranges(api: LocalApi, series: Series, warmups: int, samples: int) -> dict[str, Any]:
    """Time warm POST-free GET reads, including HTTP and response deserialization.

    Narrow and broad queries are alternated, limiting warm-cache/clock drift.
    Both operate on the same registered, fully accepted AVL series.
    """
    narrow_time = timestamp_at(series.origin, series.target)
    narrow_path = range_path(series.sensor_id, narrow_time, narrow_time, 2)
    broad_path = range_path(
        series.sensor_id, timestamp_at(series.origin, 0),
        timestamp_at(series.origin, series.count - 1), MAX_API_RANGE_PAGE,
    )
    scenarios = {"narrow": (narrow_path, 2), "broadPage": (broad_path, MAX_API_RANGE_PAGE)}
    for _ in range(warmups):
        for path, expected_length in scenarios.values():
            response = api.get(path)
            require(len(response.get("readings", [])) == expected_length, "Incorrect warmup query result")
    timings: dict[str, list[float]] = {name: [] for name in scenarios}
    for iteration in range(samples):
        order = ("narrow", "broadPage") if iteration % 2 == 0 else ("broadPage", "narrow")
        for name in order:
            path, expected_length = scenarios[name]
            start_ns = time.perf_counter_ns()
            response = api.get(path)
            elapsed_ms = (time.perf_counter_ns() - start_ns) / 1_000_000
            require(len(response.get("readings", [])) == expected_length, "Timed query returned incorrect row count")
            timings[name].append(elapsed_ms)
    return {
        "measurementScope": "client-observed GET with HTTP, JSON, server, and network overhead",
        "warmupsPerScenario": warmups,
        "scenarios": {
            name: {"returnedRowsPerSample": scenarios[name][1], **percentile_summary(timings[name])}
            for name in scenarios
        },
        "claimLimitations": "Not isolated AVL operation time; no hard CI latency threshold",
    }


def register_series(api: LocalApi, kind: str, count: int, origin: datetime, run_id: str) -> Series:
    """Use the authoritative registry; never fabricate IDs or seed a repository."""
    identifier = f"p311-{kind.lower()}-{run_id}"
    category = {"Float": "Environmental", "Integer": "PowerConsumption", "Boolean": "Actuator"}[kind]
    result = api.post("/api/sensors", {
        "name": f"P3-11 measured {kind} {run_id}",
        "deviceIdentifier": identifier,
        "identifierKind": "UniqueIdentifier",
        "deploymentLocation": {
            "facility": "P3-11 Benchmark Facility",
            "zone": "AVL Verifier - isolated test API",
            "nodeId": f"p311-{kind.lower()}-{run_id}",
        },
        "category": category,
        "telemetryDataType": kind,
    })
    sensor_id = result.get("id")
    require(isinstance(sensor_id, str) and bool(sensor_id), "Sensor registration omitted server UUID")
    require(result.get("telemetryDataType") == kind, "Registered type differs from request")
    return Series(sensor_id=sensor_id, identifier=identifier, kind=kind, count=count, origin=origin)


def seed_series(api: LocalApi, series: Series, batch_size: int) -> dict[str, Any]:
    """Insert descending timestamps in REAL accepted jagged batches of <=250.

    The duplicates come AFTER the base series, so equal-timestamp AVL buckets
    must retain their original acceptance order despite later ingestion.
    """
    accepted = 0
    requests = 0
    for offset in range(0, series.count, batch_size):
        count = min(batch_size, series.count - offset)
        batch = []
        for index in range(count):
            ordinal = series.count - 1 - (offset + index)
            batch.append(packet(series.kind, timestamp_at(series.origin, ordinal), value_for(series.kind, ordinal)))
        result = api.post(f"/api/sensors/{series.sensor_id}/telemetry/batches", {"batches": [batch]})
        require(result.get("sensorProfileId", "").lower() == series.sensor_id.lower(), "Ingestion receipt sensor mismatch")
        require(result.get("telemetryDataType") == series.kind, "Ingestion receipt type mismatch")
        require(result.get("batchCount") == 1 and result.get("acceptedPacketCount") == count, "Ingestion receipt count mismatch")
        accepted += count
        requests += 1
        if requests % 25 == 0:
            print(f"  {series.kind}: {accepted:,}/{series.count:,} base readings accepted", flush=True)
    duplicates = [packet(series.kind, timestamp_at(series.origin, series.target), value) for value in duplicates_for(series.kind)]
    receipt = api.post(f"/api/sensors/{series.sensor_id}/telemetry/batches", {"batches": [duplicates]})
    require(receipt.get("batchCount") == 1 and receipt.get("acceptedPacketCount") == 2, "Duplicate batch was not accepted")
    require(receipt.get("sensorProfileId", "").lower() == series.sensor_id.lower(), "Duplicate receipt sensor mismatch")
    require(receipt.get("telemetryDataType") == series.kind, "Duplicate receipt payload type mismatch")
    accepted += 2
    requests += 1
    require(accepted == series.accepted_count, "Accepted count did not match deterministic fixture")
    print(f"  {series.kind}: {accepted:,} accepted in {requests} HTTP POSTs", flush=True)
    return {"sensorId": series.sensor_id, "type": series.kind, "baseReadings": series.count, "acceptedReadings": accepted, "ingestionRequests": requests}


def worker_snapshot(api: LocalApi) -> dict[str, Any]:
    """Read genuine processing diagnostics, not a synthetic completion timer."""
    result = api.get("/api/realtime/processing")
    for key in ("acceptedCount", "processedCount", "pendingCount", "outstandingDispatchedCount"):
        require(type(result.get(key)) is int and result[key] >= 0, f"Worker counter {key} was invalid")
    require(result.get("workerStatus") not in ("Stopped", "Faulted"), "Real-time worker is not healthy")
    require(result.get("failure") is None, "Real-time worker reported an error")
    return result


def await_idle(api: LocalApi, timeout_seconds: int) -> dict[str, Any]:
    """Exclude a still-processing worker from the timed range-query phase."""
    deadline = time.monotonic() + timeout_seconds
    while True:
        snapshot = worker_snapshot(api)
        if snapshot["pendingCount"] == 0 and snapshot["outstandingDispatchedCount"] == 0:
            return snapshot
        require(time.monotonic() < deadline, "Worker failed to drain within timeout; no benchmark evidence written")
        time.sleep(0.25)


def verify_invalid_ranges(api: LocalApi, series: Series) -> None:
    """Malformed read-only requests must fail with HTTP 400, not mutate data."""
    first = timestamp_at(series.origin, 0)
    last = timestamp_at(series.origin, series.count - 1)
    path = f"/api/sensors/{series.sensor_id}/telemetry/range"
    for invalid_params in (
        {"fromUtc": last, "toUtc": first, "limit": "2"},
        {"fromUtc": first, "toUtc": last, "limit": "1001"},
        {"fromUtc": first, "toUtc": last, "limit": "2", "afterUtc": first},
    ):
        problem = api.get(f"{path}?{urlencode(invalid_params)}", expected_status=400)
        require(bool(problem), "Validation error returned no ProblemDetails")


def write_result(path: Path, result: dict[str, Any]) -> None:
    """Persist only VERIFIED evidence, atomically, without stale partial files."""
    path.parent.mkdir(parents=True, exist_ok=True)
    require(not path.exists(), f"Refusing to overwrite existing evidence: {path}")
    pending = path.with_name(f".{path.name}.{os.getpid()}.pending")
    try:
        pending.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.replace(pending, path)
    finally:
        pending.unlink(missing_ok=True)


def run(args: argparse.Namespace) -> dict[str, Any]:
    """Execute one isolated 10k/50k scenario, then return factual evidence."""
    api = LocalApi(args.base_url, args.request_timeout)
    topo = api.get("/api/topology")
    require(topo.get("nodeCount") == 1 and topo.get("linkCount") == 0,
            "Use a fresh local API: expected only the gateway and zero links. Restart the API before each run")
    before = await_idle(api, args.worker_timeout)
    require(before.get("ordinaryDispatchPaused") is False, "Ordinary processing is paused")
    run_id = uuid4().hex[:12]
    fixtures = (
        ("Float", args.count, FIRST_CAPTURE_UTC),
        ("Integer", args.typed_count, FIRST_CAPTURE_UTC + timedelta(days=2)),
        ("Boolean", args.typed_count, FIRST_CAPTURE_UTC + timedelta(days=3)),
    )
    series_list = [register_series(api, kind, count, origin, run_id) for kind, count, origin in fixtures]
    accepted_receipts = [seed_series(api, series, args.batch_size) for series in series_list]
    after = await_idle(api, args.worker_timeout)
    expected_total = sum(series.accepted_count for series in series_list)
    require(after["acceptedCount"] - before["acceptedCount"] == expected_total,
            "Process-wide accepted counter differs; concurrent ingestion may have occurred")
    require(after["processedCount"] - before["processedCount"] == expected_total,
            "Worker completion does not match accepted packets")
    checks = []
    for series in series_list:
        traversal = verify_full_traversal(api, series, args.page_limit)
        narrow = verify_narrow(api, series)
        recent = verify_recent_history(api, series)
        checks.append({"sensorId": series.sensor_id, "type": series.kind, "traversal": traversal, "duplicateWindow": narrow, "legacyRecent": recent})
        print(f"  {series.kind}: {traversal['indexedReadings']:,} ordered rows in {traversal['pages']} bounded AVL pages", flush=True)
    verify_invalid_ranges(api, series_list[0])
    # Confirm rejected requests did not alter the previously checked index.
    verify_narrow(api, series_list[0])
    metrics = benchmark_ranges(api, series_list[0], args.warmups, args.samples)
    return {
        "issue": "P3-11",
        "scenario": "real-api-avl-telemetry-benchmark",
        "result": "pass",
        "measuredAtUtc": datetime.now(UTC).isoformat(timespec="seconds"),
        "runId": run_id,
        "apiOrigin": api.base_url,
        "environment": {
            "platform": platform.system(),
            "architecture": platform.machine(),
            "python": platform.python_version(),
            "logicalCpuCount": os.cpu_count(),
            "apiStorage": "process-local in-memory secondary AVL index",
        },
        "requestedPrimaryDataset": args.count,
        "secondaryTypedDatasetPerSensor": args.typed_count,
        "acceptedTotalAcrossTypes": expected_total,
        "workerAcceptedDelta": after["acceptedCount"] - before["acceptedCount"],
        "workerProcessedDelta": after["processedCount"] - before["processedCount"],
        "batchSize": args.batch_size,
        "ingestion": accepted_receipts,
        "indexedChecks": checks,
        "timing": metrics,
        "invariants": {
            "descendingArrival": True,
            "duplicatesAtExistingTimestamp": 2,
            "typedSeries": list(ALLOWED_KINDS),
            "compositeCursorTraversedExactly": True,
            "originalRecentIngestionOrder": True,
            "avlInternalStructuralInvariants": "Verified separately by Domain xUnit tests, not exposed over HTTP",
            "invalidReadRequestsRejected": True,
            "noActuatorCommandsIssued": True,
        },
        "caveats": [
            "These are local HTTP end-to-end timings, not isolated AVL microbenchmarks.",
            "Broad query measures ONE 1000-row page, not end-to-end retrieval of all readings.",
            "For comparable 10k/50k runs restart the same local API using the same hardware/configuration.",
            "Timestamp range pagination is keyset-based and not a transactionally isolated multi-request snapshot.",
            "An actual load result is not an asymptotic complexity proof.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:5095")
    parser.add_argument("--count", type=int, default=10_000, help="Float base readings: 1000..50000")
    parser.add_argument("--typed-count", type=int, default=96, help="Extra Integer and Boolean base readings: 24..256 each")
    parser.add_argument("--batch-size", type=int, default=250, help="Real POST batch size: 1..250")
    parser.add_argument("--page-limit", type=int, default=1_000, help="Bounded full AVL cursor page: 1..1000")
    parser.add_argument("--samples", type=int, default=25, help="Measured samples per query scenario: 5..100")
    parser.add_argument("--warmups", type=int, default=3, help="Unmeasured warmups per query: 1..20")
    parser.add_argument("--request-timeout", type=float, default=120.0)
    parser.add_argument("--worker-timeout", type=int, default=600)
    parser.add_argument("--output", type=Path, default=None,
                        help="Only write real passing JSON; never overwrite an existing result")
    args = parser.parse_args(argv)
    try:
        require(1_000 <= args.count <= 50_000, "--count must be 1000..50000")
        require(24 <= args.typed_count <= 256, "--typed-count must be 24..256")
        require(1 <= args.batch_size <= 250, "--batch-size must be 1..250")
        require(1 <= args.page_limit <= MAX_API_RANGE_PAGE, "--page-limit must be 1..1000")
        require(5 <= args.samples <= 100, "--samples must be 5..100")
        require(1 <= args.warmups <= 20, "--warmups must be 1..20")
        require(1 <= args.request_timeout <= 300, "--request-timeout must be 1..300 seconds")
        require(30 <= args.worker_timeout <= 3600, "--worker-timeout must be 30..3600 seconds")
        print(f"P3-11: seeding {args.count:,} Float + {args.typed_count:,} Integer + {args.typed_count:,} Boolean readings (+6 ties)", flush=True)
        results = run(args)
        if args.output is not None:
            write_result(args.output, results)
            print(f"Verified evidence written: {args.output}", flush=True)
        print(json.dumps({
            "result": results["result"],
            "accepted": results["acceptedTotalAcrossTypes"],
            "indexed": sum(item["traversal"]["indexedReadings"] for item in results["indexedChecks"]),
            "narrow": results["timing"]["scenarios"]["narrow"],
            "broadPage": results["timing"]["scenarios"]["broadPage"],
        }, indent=2), flush=True)
    except (VerificationError, ValueError, OSError) as error:
        print(f"FAIL P3-11 telemetry verification: {error}", file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
