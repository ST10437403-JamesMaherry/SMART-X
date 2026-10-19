#!/usr/bin/env python3
"""Verify bounded Smart-X Part 2 load using the actual HTTP API.

This is an evidence probe, not a synthetic counter or a latency benchmark.
Every registration and telemetry packet goes through the existing endpoints.
The report is written ONLY after HTTP acceptance, worker completion,
per-device history retention, timestamp-tie and HashSet checks all succeed.

Dependencies: Python 3 standard library only.
Example (isolated Docker instance):
    python3 scripts/verify_part2_finite_load.py \
      --base-url http://localhost:8080 \
      --devices 100 --readings-per-device 100 \
      --output docs/evidence/part2/part2-load-summary.json
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import platform
import sys
from time import monotonic, sleep
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen
from uuid import uuid4

MAX_DEVICES = 100
MAX_READINGS_PER_DEVICE = 100
MAX_TOTAL_READINGS = 10_000
MAX_BATCH_SIZE = 25
POLL_SECONDS = 0.25


class VerificationError(RuntimeError):
    """A confirmed HTTP or state invariant did not match expectations."""


def require(condition: bool, message: str) -> None:
    """Raise a helpful, non-silent failure instead of publishing bad evidence."""
    if not condition:
        raise VerificationError(message)


def request_json(
    base_url: str,
    method: str,
    route: str,
    *,
    payload: dict[str, Any] | None = None,
    expected_status: int = 200,
) -> dict[str, Any] | list[Any]:
    """Issue one real HTTP request; fail if status/body violates the API contract.

    Never automatically retry POST or PATCH: an accepted write must not be
    duplicated if the connection fails while the response is in flight.
    """
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    req = Request(
        base_url.rstrip("/") + route,
        data=data,
        method=method,
        headers={"Accept": "application/json", "Content-Type": "application/json"},
    )
    try:
        with urlopen(req, timeout=30) as response:
            status = response.status
            body = response.read().decode("utf-8")
    except HTTPError as exc:
        text = exc.read(500).decode("utf-8", errors="replace")
        raise VerificationError(
            f"{method} {route} returned HTTP {exc.code}: {text}"
        ) from exc
    except (URLError, TimeoutError) as exc:
        raise VerificationError(f"{method} {route} failed: {exc}") from exc

    require(status == expected_status, f"{method} {route}: expected HTTP "
            f"{expected_status}, received {status}")
    try:
        document = json.loads(body)
    except (ValueError, TypeError) as exc:
        raise VerificationError(f"{method} {route}: response was not JSON") from exc
    require(isinstance(document, (dict, list)), f"{method} {route}: invalid JSON root")
    return document


def utc_string(value: datetime) -> str:
    """Use UTC ISO-8601 timestamps; no fake processing or capturedAt values."""
    return value.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def utc_instant(value: str) -> datetime:
    """Compare .NET DateTimeOffset values by instant, not Z vs +00:00 text."""
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def make_reading(kind: str, instant: datetime, index: int) -> dict[str, Any]:
    """Preserve zero/false and use exactly one non-null typed payload field."""
    value: dict[str, Any] = {
        "capturedAtUtc": utc_string(instant),
        "floatValue": None,
        "integerValue": None,
        "booleanValue": None,
    }
    if kind == "Float":
        value["floatValue"] = 20 + index / 1000
    elif kind == "Integer":
        value["integerValue"] = 1000 + index
    else:
        value["booleanValue"] = index % 2 == 0
    return value


def typed_value(reading: dict[str, Any], kind: str) -> int | float | bool | None:
    return reading["floatValue" if kind == "Float" else
                   "integerValue" if kind == "Integer" else "booleanValue"]


def processing_snapshot(base_url: str) -> dict[str, Any]:
    response = request_json(base_url, "GET", "/api/realtime/processing")
    require(isinstance(response, dict), "Processing response was not an object")
    require(not response.get("failure"), f"Worker failure: {response.get('failure')}")
    require(response.get("workerStatus") not in ("Faulted", "Stopped"),
            f"Worker not usable: {response.get('workerStatus')}")
    return response


def get_history(base_url: str, sensor_id: str) -> dict[str, Any]:
    result = request_json(
        base_url, "GET", f"/api/realtime/devices/{quote(sensor_id)}/history?limit=100&order=asc"
    )
    require(isinstance(result, dict), "History response was not an object")
    return result


def wait_until_idle(base_url: str, deadline: float) -> dict[str, Any]:
    """Use worker counters as a readiness signal, not fabricated completions."""
    latest = processing_snapshot(base_url)
    while latest["pendingCount"] or latest["outstandingDispatchedCount"]:
        require(monotonic() < deadline,
                "Timed out with pending work; inspect /api/realtime/processing")
        sleep(POLL_SECONDS)
        latest = processing_snapshot(base_url)
    return latest


def verify_one_history(
    base_url: str,
    sensor: dict[str, Any],
    expected_readings: list[dict[str, Any]],
    kind: str,
) -> dict[str, Any]:
    """Check Part 1 ingestion order and independent processed-event history.

    Only the bounded 100-per-device query is needed, even for the 10k load.
    Each device stores <= 100 events, so this does not depend on the 100-item
    worker diagnostic window and cannot mistake request acceptance for work.
    """
    sensor_id = sensor["id"]
    expected_count = len(expected_readings)
    history = get_history(base_url, sensor_id)
    events = history["events"]
    require(history["totalRecordedEvents"] == expected_count,
            f"{sensor_id}: expected {expected_count} recorded events; "
            f"got {history['totalRecordedEvents']}")
    require(len(events) == expected_count, f"{sensor_id}: history was truncated")
    require(len({event["eventId"] for event in events}) == expected_count,
            f"{sensor_id}: event IDs were duplicated")
    require(all(event["kind"] == "TelemetryProcessed" for event in events),
            f"{sensor_id}: non-telemetry events appeared in the load history")

    actual_capture_times = [utc_instant(event["telemetry"]["capturedAtUtc"]) for event in events]
    expected_capture_times = [utc_instant(reading["capturedAtUtc"]) for reading in expected_readings]
    require(actual_capture_times == expected_capture_times,
            f"{sensor_id}: chronology / equal-time event retention changed")
    require(all(event["telemetry"]["telemetryDataType"] == kind for event in events),
            f"{sensor_id}: telemetry type changed during processing")
    require([typed_value(event["telemetry"], kind) for event in events] ==
            [typed_value(reading, kind) for reading in expected_readings],
            f"{sensor_id}: processed telemetry content was lost or changed")

    if expected_count >= 2:
        tie_index = min(6, expected_count - 1)
        repeated = utc_instant(expected_readings[tie_index]["capturedAtUtc"])
        require(actual_capture_times.count(repeated) >= 2,
                f"{sensor_id}: equal-time events did not both survive")

    # Part 1 intentionally returns telemetry in original ingestion order.
    recent = request_json(
        base_url, "GET", f"/api/sensors/{quote(sensor_id)}/telemetry?limit=100"
    )
    require(isinstance(recent, list) and len(recent) == expected_count,
            f"{sensor_id}: Part 1 telemetry was truncated")
    require([utc_instant(reading["capturedAtUtc"]) for reading in recent] == expected_capture_times,
            f"{sensor_id}: Part 1 telemetry ingestion order changed")
    require([typed_value(reading, kind) for reading in recent] ==
            [typed_value(reading, kind) for reading in expected_readings],
            f"{sensor_id}: Part 1 payload content changed")

    return {
        "sensorId": sensor_id,
        "telemetryType": kind,
        "submittedReadings": expected_count,
        "processedHistoryEvents": len(events),
        "retainedEqualTimestampEvents": expected_count >= 2,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--devices", type=int, default=3)
    parser.add_argument("--readings-per-device", type=int, default=12)
    parser.add_argument("--timeout-seconds", type=int, default=240)
    parser.add_argument("--output", type=Path, default=None,
                        help="Write actual verified JSON only after all checks pass")
    args = parser.parse_args()

    require(1 <= args.devices <= MAX_DEVICES, "--devices must be 1..100")
    require(2 <= args.readings_per_device <= MAX_READINGS_PER_DEVICE,
            "--readings-per-device must be 2..100 for tie verification")
    require(args.devices * args.readings_per_device <= MAX_TOTAL_READINGS,
            "Load cannot exceed 10,000 readings")
    require(args.timeout_seconds >= 30, "--timeout-seconds must be at least 30")

    deadline = monotonic() + args.timeout_seconds
    began_utc = datetime.now(timezone.utc)
    start = monotonic()
    before = wait_until_idle(args.base_url, deadline)
    require(not before["ordinaryDispatchPaused"],
            "Ordinary dispatch is paused; resume it before the load check")

    run_id = uuid4().hex[:12]
    specs = [
        ("Float", "Environmental"),
        ("Integer", "PowerConsumption"),
        ("Boolean", "Actuator"),
    ]
    registered: list[tuple[dict[str, Any], list[dict[str, Any]], str]] = []
    submitted = 0
    accepted = 0
    accepted_batches = 0
    kind_counts: Counter[str] = Counter()

    for device_index in range(args.devices):
        kind, category = specs[device_index % len(specs)]
        sensor_identifier = f"p211-load-{run_id}-{device_index:03d}"
        registration = request_json(args.base_url, "POST", "/api/sensors", payload={
            "name": f"P2-11 verified {kind} load {device_index + 1}",
            "deviceIdentifier": sensor_identifier,
            "identifierKind": "UniqueIdentifier",
            "deploymentLocation": {
                "facility": "Smart-X Load Verification",
                "zone": "Part 2 release test",
                "nodeId": f"node-{run_id}-{device_index:03d}",
            },
            "category": category,
            "telemetryDataType": kind,
        }, expected_status=201)
        require(isinstance(registration, dict) and isinstance(registration.get("id"), str),
                "Registration did not return a valid device ID")
        sensor_id = registration["id"]
        request_json(args.base_url, "PATCH", f"/api/sensors/{quote(sensor_id)}/status",
                     payload={"status": "Online"})

        # All capture timestamps are in the past. A deliberate equal-timestamp
        # pair must occupy two separate events in SortedDictionary buckets.
        captured_base = datetime.now(timezone.utc) - timedelta(
            seconds=args.readings_per_device + 120
        )
        tie_index = min(6, args.readings_per_device - 1)
        readings = [
            make_reading(
                kind,
                captured_base + timedelta(seconds=index if index != tie_index else index - 1),
                index,
            )
            for index in range(args.readings_per_device)
        ]

        # Genuine jagged batches capped at 25; don't allocate all devices'
        # packets at once. Including the shorter tail preserves uneven input.
        batches = [
            readings[offset:offset + MAX_BATCH_SIZE]
            for offset in range(0, len(readings), MAX_BATCH_SIZE)
        ]
        response = request_json(
            args.base_url, "POST", f"/api/sensors/{quote(sensor_id)}/telemetry/batches",
            payload={"batches": batches}, expected_status=201
        )
        require(isinstance(response, dict), "Batch receipt was not a JSON object")
        require(response["sensorProfileId"] == sensor_id, "Batch receipt device mismatch")
        require(response["telemetryDataType"] == kind, "Batch receipt type mismatch")
        require(response["batchCount"] == len(batches), "Batch count mismatch")
        require(response["acceptedPacketCount"] == len(readings),
                "Server did not accept every packet")

        submitted += len(readings)
        accepted += response["acceptedPacketCount"]
        accepted_batches += response["batchCount"]
        kind_counts[kind] += len(readings)
        registered.append((registration, readings, kind))
        print(f"[{device_index + 1}/{args.devices}] {kind}: accepted {len(readings)}", flush=True)

    require(submitted == accepted, "Submitted / accepted counters do not match")

    # The worker may still be processing when the final HTTP 201 arrives.
    # Wait for the real worker, then verify per-device history independently.
    last = processing_snapshot(args.base_url)
    while (
        last["processedCount"] - before["processedCount"] < accepted
        or last["pendingCount"]
        or last["outstandingDispatchedCount"]
    ):
        require(monotonic() < deadline,
                f"Timed out: accepted {accepted}; processed delta "
                f"{last['processedCount'] - before['processedCount']}; "
                f"pending {last['pendingCount']}")
        sleep(POLL_SECONDS)
        last = processing_snapshot(args.base_url)

    accepted_delta = last["acceptedCount"] - before["acceptedCount"]
    processed_delta = last["processedCount"] - before["processedCount"]
    require(accepted_delta == accepted,
            "Process-wide accepted delta differs from this run; other traffic may be active")
    require(processed_delta == accepted,
            "Process-wide processed delta differs; worker completion mismatch")
    require(last["pendingCount"] == 0 and last["outstandingDispatchedCount"] == 0,
            "Worker has outstanding load")

    device_details = [
        verify_one_history(args.base_url, sensor, readings, kind)
        for sensor, readings, kind in registered
    ]

    # Check authoritative unresolved conditions only for the new run's devices.
    # Missing Configuration may be present; it must be a single active key.
    active = request_json(
        args.base_url, "GET", "/api/realtime/snapshot/active-conditions"
    )
    require(isinstance(active, dict), "Active-condition response was not an object")
    created_ids = {sensor["id"] for sensor, _, _ in registered}
    own_conditions = [
        issue for issue in active["activeConditions"]
        if issue["sensorProfileId"] in created_ids
    ]
    memberships = [issue["membershipKey"] for issue in own_conditions]
    require(len(memberships) == len(set(memberships)),
            "Duplicate active HashSet membership detected")

    completed_utc = datetime.now(timezone.utc)
    summary: dict[str, Any] = {
        "check": "P2-11 actual HTTP finite load verification",
        "result": "PASS",
        "runId": run_id,
        "baseUrl": args.base_url.rstrip("/"),
        "startedAtUtc": utc_string(began_utc),
        "completedAtUtc": utc_string(completed_utc),
        "observedElapsedMilliseconds": round((monotonic() - start) * 1000),
        "environment": {
            "platform": platform.platform(),
            "pythonVersion": platform.python_version(),
        },
        "submittedDevices": args.devices,
        "registeredDevices": len(registered),
        "readingsPerDevice": args.readings_per_device,
        "submittedReadings": submitted,
        "httpAcceptedReadings": accepted,
        "acceptedBatchCount": accepted_batches,
        "workerAcceptedDelta": accepted_delta,
        "workerProcessedDelta": processed_delta,
        "workerPendingAtEnd": last["pendingCount"],
        "workerOutstandingAtEnd": last["outstandingDispatchedCount"],
        "workerFailure": last["failure"],
        "perTypeReadings": dict(sorted(kind_counts.items())),
        "processedHistoryReadings": sum(
            entry["processedHistoryEvents"] for entry in device_details
        ),
        "verifiedActiveMembershipCount": len(own_conditions),
        "verifiedDistinctActiveMembershipCount": len(set(memberships)),
        "deviceDetails": device_details,
        "notes": [
            "Every device and telemetry batch used the real Smart-X HTTP API.",
            "Process-wide deltas are valid only for the isolated, initially idle run.",
            "Historical processing and Part 1 telemetry were checked per device.",
            "A same-timestamp pair was retained in each device's history.",
            "This is an observed finite-load demonstration, not a latency benchmark.",
            "Devices, worker counters and history are intentionally process-local.",
        ],
    }
    serialized = json.dumps(summary, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized, encoding="utf-8")
        print(f"PASS — verified report: {args.output}", file=sys.stderr)
    print(serialized)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (VerificationError, KeyError, TypeError, ValueError, OSError) as error:
        print(f"FAIL — no evidence report written: {error}", file=sys.stderr)
        sys.exit(1)
