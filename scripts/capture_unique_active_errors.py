#!/usr/bin/env python3
"""Capture verified P2-06 HashSet active-condition lifecycle evidence from local Smart-X.

Runs real HTTP operations against two newly registered demonstration sensors.
Produces no evidence artifacts unless every source-of-truth assertion succeeds.
Uses Python's standard library only, with no cloud or external network calls.
"""

from __future__ import annotations

import argparse
import html
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any


class EvidenceError(RuntimeError):
    """Raised when the actual API fails an evidence invariant."""


def require(condition: bool, message: str) -> None:
    """Stop the capture instead of writing unsupported evidence."""
    if not condition:
        raise EvidenceError(message)


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def as_utc_text(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat()


def escape(value: object) -> str:
    return html.escape(str(value), quote=True)


def canonical_key(sensor_id: str, condition_type: str) -> str:
    """Mirror the Domain key: Guid:N + ':' + validated condition type."""
    return f"{uuid.UUID(sensor_id).hex}:{condition_type}"


class LocalSmartX:
    """Minimal, local-only JSON and multipart client for the actual Smart-X API."""

    def __init__(self, base_url: str) -> None:
        parsed = urllib.parse.urlsplit(base_url)
        require(parsed.scheme in ("http", "https") and
                parsed.hostname in ("localhost", "127.0.0.1", "::1") and
                parsed.path in ("", "/") and not parsed.query and
                not parsed.fragment and not parsed.username and not parsed.password,
                "The evidence script may only call a local URL (http://localhost:8080).")
        self.base_url = base_url.rstrip("/")
        # Do not send local IoT evidence through environment-configured proxies.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(self, path: str, method: str = "GET", body: Any = None,
                expected: int = 200, multipart: bytes | None = None,
                content_type: str | None = None) -> Any:
        require(path.startswith("/") and not path.startswith("//"),
                "API path must be a local relative path.")
        require(not (body is not None and multipart is not None),
                "Request cannot have both JSON and multipart bodies.")
        payload = multipart if multipart is not None else (
            None if body is None else json.dumps(body).encode("utf-8")
        )
        headers = {"Accept": "application/json"}
        if payload is not None:
            headers["Content-Type"] = content_type or "application/json"
        request = urllib.request.Request(
            self.base_url + path, data=payload, method=method, headers=headers
        )
        try:
            with self.opener.open(request, timeout=20) as response:
                status, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read()
        except urllib.error.URLError as error:
            raise EvidenceError(
                f"Local Smart-X API unreachable: {error.reason}"
            ) from error
        try:
            result = json.loads(raw.decode("utf-8")) if raw else None
        except (ValueError, UnicodeDecodeError) as error:
            raise EvidenceError(
                f"{method} {path} returned non-JSON HTTP {status}"
            ) from error
        require(status == expected,
                f"{method} {path}: HTTP {status}, expected {expected}; response: {result}")
        return result

    def processing(self) -> dict[str, Any]:
        state = self.request("/api/realtime/processing")
        require(state["failure"] is None and
                state["workerStatus"] not in ("Faulted", "Stopped"),
                "The real telemetry worker is unavailable or faulted.")
        require(state["acceptedCount"] == state["pendingCount"] +
                state["processedCount"] + state["outstandingDispatchedCount"],
                "Real-time accepted-work accounting is inconsistent.")
        require(state["dispatchedCount"] == state["processedCount"] +
                state["outstandingDispatchedCount"],
                "Real-time dispatch accounting is inconsistent.")
        return state

    def register_float_sensor(self, name: str) -> dict[str, Any]:
        identifier = "p206-evidence-" + uuid.uuid4().hex
        return self.request("/api/sensors", method="POST", expected=201, body={
            "name": name,
            "deviceIdentifier": identifier,
            "identifierKind": "UniqueIdentifier",
            "deploymentLocation": {
                "facility": "Evidence Facility",
                "zone": "HashSet Lifecycle",
                "nodeId": identifier,
            },
            "category": "Environmental",
            "telemetryDataType": "Float",
        })

    def upload_configuration(self, sensor_id: str) -> dict[str, Any]:
        """Send an actual file through the encrypted Part 1 multipart endpoint."""
        boundary = "smartx-p206-" + uuid.uuid4().hex
        contents = json.dumps({"enabled": True, "sampleRateSeconds": 5})
        lines = [
            f"--{boundary}\r\n",
            'Content-Disposition: form-data; name="file"; filename="p206-evidence.json"\r\n',
            "Content-Type: application/json\r\n\r\n",
            contents + "\r\n",
            f"--{boundary}--\r\n",
        ]
        return self.request(
            f"/api/sensors/{sensor_id}/attachments?category=Configuration",
            method="POST", expected=201,
            multipart="".join(lines).encode("utf-8"),
            content_type=f"multipart/form-data; boundary={boundary}",
        )

    def change_status(self, sensor_id: str, status: str) -> dict[str, Any]:
        return self.request(
            f"/api/sensors/{sensor_id}/status", method="PATCH",
            body={"status": status},
        )

    def ingest_float(self, sensor_id: str, value: float,
                     captured: datetime) -> dict[str, Any]:
        return self.request(
            f"/api/sensors/{sensor_id}/telemetry", method="POST", expected=201,
            body={"capturedAtUtc": as_utc_text(captured), "floatValue": value},
        )

    def acknowledge(self, issue_id: str) -> dict[str, Any]:
        # The original Part 1 operation requires no request body.
        return self.request(
            f"/api/dashboard/issues/{issue_id}/acknowledge", method="POST"
        )

    def realtime(self) -> dict[str, Any]:
        return self.request("/api/realtime/snapshot/active-conditions")

    def dashboard(self, include_resolved: bool = False) -> dict[str, Any]:
        suffix = "?includeResolved=true" if include_resolved else ""
        return self.request("/api/dashboard/issues" + suffix)

    def history(self, sensor_id: str) -> dict[str, Any]:
        return self.request(
            f"/api/realtime/devices/{sensor_id}/history?limit=500&order=asc"
        )

    def wait_history(self, sensor_id: str, minimum: int) -> dict[str, Any]:
        """Poll the real worker's history projection, avoiding completion races."""
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            result = self.history(sensor_id)
            if result["totalRecordedEvents"] >= minimum:
                return result
            self.processing()  # Fail early on a worker fault.
            time.sleep(0.05)
        raise EvidenceError(
            f"Timed out waiting for {minimum} actual processed events on {sensor_id}."
        )


def assert_pair(api: LocalSmartX, device_ids: set[str]) -> dict[str, Any]:
    """Read Part 1 and P2-06 separately and verify identical active membership.

    Data stored in the evidence JSON is scoped to our demonstration sensors
    so we do not accidentally capture unrelated devices' private metadata.
    """
    realtime = api.realtime()
    dashboard = api.dashboard()
    all_rt = realtime["activeConditions"]
    all_p1 = dashboard["issues"]

    # Global counters must agree with actual rows, even when another sensor
    # happens to be registered in this local API process.
    require(realtime["activeConditionCount"] == len(all_rt),
            "Part 2 active-condition total does not equal its row count.")
    require(realtime["unacknowledgedConditionCount"] ==
            sum(row["state"] == "Active" for row in all_rt),
            "Part 2 unacknowledged count is inconsistent.")
    require(dashboard["activeIssueCount"] == len(all_p1),
            "Part 1 active issue total is inconsistent.")
    require(dashboard["unacknowledgedIssueCount"] ==
            sum(row["state"] == "Active" for row in all_p1),
            "Part 1 unacknowledged count is inconsistent.")

    rt_rows = [row for row in all_rt if row["sensorProfileId"] in device_ids]
    p1_rows = [row for row in all_p1 if row["sensorProfileId"] in device_ids]
    require(len({row["membershipKey"] for row in rt_rows}) == len(rt_rows),
            "A sensor/condition key has duplicate active rows.")
    by_id = {row["id"]: row for row in p1_rows}
    require(len(rt_rows) == len(p1_rows) == len(by_id),
            "Part 1 and Part 2 disagree on the demo sensors' active issue count.")
    for row in rt_rows:
        matching = by_id.get(row["issueId"])
        require(matching is not None, "P2-06 active issue missing from Part 1.")
        require(row["membershipKey"] == canonical_key(
            row["sensorProfileId"], row["conditionType"]),
            "Active key differs from the Domain Guid:N:IssueType key.")
        for p2_field, p1_field in (
            ("conditionType", "type"), ("severity", "severity"),
            ("state", "state"), ("occurrenceCount", "occurrenceCount"),
        ):
            require(row[p2_field] == matching[p1_field],
                    f"Part 1/P2-06 mismatch in '{p2_field}'.")
        require(row["state"] in ("Active", "Acknowledged"),
                "A resolved condition remained in active HashSet membership.")

    # Store the authoritative API's actual filtered rows, not fabricated rows.
    return {
        "realtime": {
            "generatedAtUtc": realtime["generatedAtUtc"],
            "activeConditionCount": len(rt_rows),
            "unacknowledgedConditionCount": sum(
                row["state"] == "Active" for row in rt_rows
            ),
            "activeConditions": rt_rows,
        },
        "partOne": {
            "generatedAtUtc": dashboard["generatedAtUtc"],
            "activeIssueCount": len(p1_rows),
            "unacknowledgedIssueCount": sum(
                row["state"] == "Active" for row in p1_rows
            ),
            "issues": p1_rows,
        },
    }


def only_issue(snapshot: dict[str, Any], sensor_id: str,
               condition_type: str) -> dict[str, Any]:
    rows = [row for row in snapshot["realtime"]["activeConditions"]
            if row["sensorProfileId"] == sensor_id and
            row["conditionType"] == condition_type]
    require(len(rows) == 1,
            f"Expected one active '{condition_type}' for {sensor_id}; found {len(rows)}.")
    return rows[0]


def no_issues(snapshot: dict[str, Any], sensor_id: str) -> None:
    require(not any(row["sensorProfileId"] == sensor_id
                    for row in snapshot["realtime"]["activeConditions"]),
            f"Sensor {sensor_id} still has an active condition after recovery.")


def capture(api: LocalSmartX) -> dict[str, Any]:
    """Exercise actual membership, escalation, acknowledgement and recovery."""
    health = api.request("/api/health")
    require(health["status"] == "Healthy", "Smart-X health endpoint is not healthy.")
    baseline = api.processing()
    require(baseline["pendingCount"] == 0 and
            baseline["outstandingDispatchedCount"] == 0,
            "Use a quiet local API without unrelated pending telemetry work.")
    require(not baseline["ordinaryDispatchPaused"],
            "Resume ordinary dispatch before evidence capture; the script will not change it.")

    # The endpoint must already be live before we create demonstration data.
    assert_pair(api, set())
    primary = api.register_float_sensor("P2-06 HashSet / Telemetry")
    primary_id = primary["id"]
    scope = {primary_id}
    phases: list[dict[str, Any]] = []

    def phase(label: str) -> dict[str, Any]:
        observation = assert_pair(api, scope)
        phases.append({"label": label, **observation})
        return observation

    # An unchanged missing configuration is a state, not recurring work.
    missing = phase("01 / Configuration missing")
    missing_row = only_issue(missing, primary_id, "ConfigurationIssue")
    require(missing_row["occurrenceCount"] == 1 and
            missing_row["state"] == "Active", "Initial configuration issue is invalid.")
    for _ in range(3):
        repeat = assert_pair(api, scope)
        duplicate = only_issue(repeat, primary_id, "ConfigurationIssue")
        require(duplicate["issueId"] == missing_row["issueId"] and
                duplicate["occurrenceCount"] == 1,
                "Repeated snapshot reads duplicated unchanged configuration work.")
    phase("02 / Repeated reads, one activation")

    acknowledged_config = api.acknowledge(missing_row["issueId"])
    require(acknowledged_config["id"] == missing_row["issueId"] and
            acknowledged_config["state"] == "Acknowledged",
            "Configuration acknowledgement failed.")
    config_ack = phase("03 / Acknowledged, still active")
    ack_issue = only_issue(config_ack, primary_id, "ConfigurationIssue")
    require(ack_issue["state"] == "Acknowledged" and
            ack_issue["membershipKey"] == missing_row["membershipKey"] and
            ack_issue["issueId"] == missing_row["issueId"] and
            config_ack["realtime"]["activeConditionCount"] == 1 and
            config_ack["realtime"]["unacknowledgedConditionCount"] == 0,
            "Acknowledgement incorrectly removed unresolved membership.")

    config_attachment = api.upload_configuration(primary_id)
    config_recovered = phase("04 / Configuration evidence restores health")
    no_issues(config_recovered, primary_id)

    # Use explicit, ascending capture times to exercise the real Part 1
    # anomaly policy: 20% warning, 50% critical, three spikes -> repeated
    # failure, and two stable comparisons -> confirmed recovery.
    captured_base = utc_now() - timedelta(minutes=3)
    observations: list[dict[str, Any]] = []
    measurements = [100.0, 140.0, 300.0, 700.0, 701.0, 702.0, 1500.0]

    def send(value: float, index: int) -> None:
        observations.append(api.ingest_float(
            primary_id, value, captured_base + timedelta(seconds=10 * index)
        ))

    send(measurements[0], 0)  # Initial baseline; no anomaly yet.
    no_issues(phase("05 / Baseline telemetry"), primary_id)

    send(measurements[1], 1)  # 100 -> 140: Warning (>20%, <50%).
    warning = phase("06 / Warning: first activation")
    first_spike = only_issue(warning, primary_id, "TelemetrySpike")
    require(first_spike["severity"] == "Warning" and
            first_spike["state"] == "Active" and
            first_spike["occurrenceCount"] == 1,
            "First numeric anomaly did not create one Warning spike.")

    send(measurements[2], 2)  # 140 -> 300: Critical (more than 50%).
    critical = phase("07 / Critical: same issue escalates")
    increased = only_issue(critical, primary_id, "TelemetrySpike")
    require(increased["issueId"] == first_spike["issueId"] and
            increased["membershipKey"] == first_spike["membershipKey"] and
            increased["severity"] == "Critical" and
            increased["occurrenceCount"] == 2 and
            critical["realtime"]["activeConditionCount"] == 1,
            "Critical escalation created a duplicate issue or lost its count.")

    ack_spike = api.acknowledge(first_spike["issueId"])
    require(ack_spike["state"] == "Acknowledged",
            "Spike acknowledgement failed.")
    spike_ack = phase("08 / Acknowledgement retains membership")
    acknowledged = only_issue(spike_ack, primary_id, "TelemetrySpike")
    require(acknowledged["state"] == "Acknowledged" and
            acknowledged["issueId"] == first_spike["issueId"] and
            spike_ack["realtime"]["activeConditionCount"] == 1 and
            spike_ack["realtime"]["unacknowledgedConditionCount"] == 0,
            "Spike acknowledgement incorrectly resolved the active condition.")

    send(measurements[3], 3)  # Third real anomaly reaches repeated-failure threshold.
    repeated_phase = phase("09 / Third anomaly: repeated failure")
    repeated_spike = only_issue(repeated_phase, primary_id, "TelemetrySpike")
    repeated_failure = only_issue(repeated_phase, primary_id, "RepeatedFailure")
    require(repeated_spike["issueId"] == first_spike["issueId"] and
            repeated_spike["state"] == "Acknowledged" and
            repeated_spike["occurrenceCount"] == 3 and
            repeated_failure["occurrenceCount"] == 1 and
            repeated_failure["severity"] == "Critical" and
            repeated_phase["realtime"]["activeConditionCount"] == 2 and
            repeated_phase["realtime"]["unacknowledgedConditionCount"] == 1,
            "Repeated telemetry failures did not retain two distinct active keys.")

    send(measurements[4], 4)  # One stable reading is insufficient.
    first_stable = phase("10 / First stable reading: still active")
    require({row["issueId"] for row in first_stable["realtime"]["activeConditions"]}
            == {first_spike["issueId"], repeated_failure["issueId"]},
            "A single stable observation prematurely cleared the conditions.")

    send(measurements[5], 5)  # Second stable reading resolves both.
    recovered = phase("11 / Second stable reading: recovered")
    no_issues(recovered, primary_id)

    historical_after_recovery = api.dashboard(include_resolved=True)
    for old_id in (missing_row["issueId"], first_spike["issueId"],
                   repeated_failure["issueId"]):
        found = [row for row in historical_after_recovery["issues"]
                 if row["sensorProfileId"] == primary_id and row["id"] == old_id]
        require(len(found) == 1 and found[0]["state"] == "Resolved" and
                found[0]["resolvedAtUtc"] is not None,
                "Recovered episode was not retained in the Part 1 resolved history.")

    send(measurements[6], 6)  # Recurrence is a new activation episode.
    recurrence = phase("12 / Later spike: fresh episode")
    new_spike = only_issue(recurrence, primary_id, "TelemetrySpike")
    require(new_spike["membershipKey"] == first_spike["membershipKey"] and
            new_spike["issueId"] != first_spike["issueId"] and
            new_spike["occurrenceCount"] == 1 and
            new_spike["state"] == "Active" and
            recurrence["realtime"]["activeConditionCount"] == 1,
            "Recurrence must reuse the key but create a fresh issue ID.")

    # Device isolation: an independent sensor can disconnect without
    # overwriting the first sensor's new spike episode.
    secondary = api.register_float_sensor("P2-06 HashSet / Disconnect")
    secondary_id = secondary["id"]
    api.upload_configuration(secondary_id)
    api.change_status(secondary_id, "Online")
    scope.add(secondary_id)
    isolated_ready = phase("13 / Second sensor configured and Online")
    require(len(isolated_ready["realtime"]["activeConditions"]) == 1,
            "The configured Online second sensor should have no active conditions.")

    api.change_status(secondary_id, "Offline")
    disconnected_phase = phase("14 / Second sensor disconnected")
    disconnected = only_issue(disconnected_phase, secondary_id, "SensorDisconnected")
    require(len(disconnected_phase["realtime"]["activeConditions"]) == 2 and
            disconnected["state"] == "Active" and
            disconnected["severity"] == "Warning" and
            only_issue(disconnected_phase, primary_id, "TelemetrySpike")["issueId"]
            == new_spike["issueId"],
            "Disconnected sensor was not isolated from the first sensor's spike.")

    api.acknowledge(disconnected["issueId"])
    disconnect_ack = phase("15 / Disconnect acknowledged, still active")
    ack_disconnect = only_issue(disconnect_ack, secondary_id, "SensorDisconnected")
    require(ack_disconnect["state"] == "Acknowledged" and
            ack_disconnect["issueId"] == disconnected["issueId"] and
            len(disconnect_ack["realtime"]["activeConditions"]) == 2,
            "Acknowledged disconnect incorrectly left the membership set.")

    api.change_status(secondary_id, "Online")
    online = phase("16 / Online recovery preserves other sensor")
    no_issues(online, secondary_id)
    require(len(online["realtime"]["activeConditions"]) == 1 and
            only_issue(online, primary_id, "TelemetrySpike")["issueId"]
            == new_spike["issueId"],
            "Offline/Online recovery changed an unrelated active condition.")

    history_after_disconnect = api.dashboard(include_resolved=True)
    resolved_disconnect = [row for row in history_after_disconnect["issues"]
                           if row["id"] == disconnected["issueId"] and
                           row["sensorProfileId"] == secondary_id]
    require(len(resolved_disconnect) == 1 and
            resolved_disconnect[0]["state"] == "Resolved",
            "Offline/Online recovery did not preserve the resolved disconnect episode.")

    # Independent P2-05 and Part 1 regression: accepted values, chronological
    # processed history and original ingestion ordering remain unchanged.
    actual_history = api.wait_history(primary_id, len(measurements))
    part_one_telemetry = api.request(
        f"/api/sensors/{primary_id}/telemetry?limit=100"
    )
    require([row["floatValue"] for row in observations] == measurements,
            "Telemetry acceptance responses differ from submitted values.")
    require([row["floatValue"] for row in part_one_telemetry] == measurements,
            "Part 1 telemetry ingestion order or payloads changed.")
    require(actual_history["totalRecordedEvents"] == len(measurements) and
            len(actual_history["events"]) == len(measurements) and
            actual_history["retainedEventCount"] == len(measurements),
            "P2-05 did not record all seven actually processed packets.")
    require([entry["telemetry"]["floatValue"]
             for entry in actual_history["events"]] == measurements,
            "P2-05 chronological telemetry changed ordering or typed values.")
    require(all(entry["kind"] == "TelemetryProcessed" and
                entry["targetDeviceId"] == primary_id
                for entry in actual_history["events"]),
            "P2-05 events do not correspond to the demonstration sensor.")
    require(len({entry["eventId"] for entry in actual_history["events"]})
            == len(measurements), "Operational history contains duplicate event IDs.")

    require(api.processing()["failure"] is None,
            "Processing failed during the lifecycle evidence capture.")

    return {
        "captureTimestampUtc": as_utc_text(utc_now()),
        "localApiBaseUrl": api.base_url,
        "primarySensor": primary,
        "secondarySensor": secondary,
        "configurationAttachment": config_attachment,
        "acceptedTelemetry": observations,
        "partOneTelemetry": part_one_telemetry,
        "processedChronologicalHistory": actual_history,
        "phaseSnapshots": phases,
        "historicalIssues": [row for row in history_after_disconnect["issues"]
                             if row["sensorProfileId"] in scope],
        "invariants": {
            "originalConfigurationIssueId": missing_row["issueId"],
            "originalSpikeIssueId": first_spike["issueId"],
            "recurrentSpikeIssueId": new_spike["issueId"],
            "stableSpikeMembershipKey": first_spike["membershipKey"],
            "repeatedFailureIssueId": repeated_failure["issueId"],
            "disconnectIssueId": disconnected["issueId"],
            "oneOriginalSpikeEpisodeForThreeReports": True,
            "warningEscalatedToCritical": True,
            "acknowledgedConditionsStayedActive": True,
            "recoveryRequiredTwoStableReadings": True,
            "recurrenceCreatedNewIssueId": True,
            "crossDeviceMembershipRemainedIndependent": True,
            "partOneAndPartTwoSnapshotsAgreedAtEveryPhase": True,
            "partOneAndP205TelemetryWerePreserved": True,
        },
    }


def render_html(data: dict[str, Any]) -> str:
    """Render an accessible, static near-black evidence report from API facts."""
    phases = data["phaseSnapshots"]
    invariants = data["invariants"]
    primary_id = data["primarySensor"]["id"]
    secondary_id = data["secondarySensor"]["id"]

    def phase_row(number: int) -> str:
        phase = phases[number]
        parts = []
        for issue in phase["realtime"]["activeConditions"]:
            kind = issue["conditionType"]
            status = issue["state"]
            parts.append(
                "<span class='issue'>"
                f"<strong>{escape(kind)}</strong>"
                f"<small>{escape(issue['severity'])} / {escape(status)} / "
                f"{escape(issue['occurrenceCount'])} occurrence(s)</small>"
                "</span>"
            )
        issues_html = "".join(parts) or "<span class='muted'>No active conditions</span>"
        return (
            "<tr>"
            f"<td class='mono n'>{number + 1:02d}</td>"
            f"<td>{escape(phase['label'].split(' / ', 1)[1])}</td>"
            f"<td class='mono n'>{phase['realtime']['activeConditionCount']}</td>"
            f"<td class='mono n'>{phase['realtime']['unacknowledgedConditionCount']}</td>"
            f"<td>{issues_html}</td>"
            "</tr>"
        )

    # A curated selection of the recorded 16 phases fits in one screenshot.
    selected = [0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15]
    rows = "\n".join(phase_row(index) for index in selected)
    original = invariants["originalSpikeIssueId"]
    recurrence = invariants["recurrentSpikeIssueId"]
    stable_key = invariants["stableSpikeMembershipKey"]
    prior = data["processedChronologicalHistory"]["totalRecordedEvents"]
    verification = (
        "<li>Repeated configuration reads generated one episode, not duplicate activations.</li>"
        "<li>Three real spike readings reused one HashSet key and one original issue ID; severity escalated.</li>"
        "<li>Acknowledgement retained membership; two stable readings cleared the condition.</li>"
        "<li>Recurrence reused the key but created a new issue ID. Separate sensor disconnects remained isolated.</li>"
        "<li>Part 1 dashboard and Part 2 active snapshots agreed in every captured phase.</li>"
        "<li>All seven typed Float packets remain intact in Part 1 and P2-05 history.</li>"
    )
    return f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Smart-X P2-06 / Verified unique active conditions</title>
<style>
*{{box-sizing:border-box}}
:root{{color-scheme:dark}}
body{{margin:0;background:#0a0a0a;color:#f4f0e8;font-family:Georgia,serif}}
main{{max-width:1400px;margin:0 auto;padding:43px 54px 36px}}
.mono,th,td.n,.eyebrow,.meta,.marker,small{{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}}
.eyebrow{{font-size:11px;letter-spacing:.13em;color:#dbb679;text-transform:uppercase}}
h1{{font-size:46px;line-height:1.06;letter-spacing:-.035em;font-weight:400;margin:12px 0}}
.lead{{color:#bcb7af;font-size:16px;line-height:1.5;max-width:1080px;margin:0 0 23px}}
.line{{border:0;border-top:1px solid #333;margin:25px 0}}
.metrics{{display:grid;grid-template-columns:repeat(4,1fr);gap:23px}}
.metric{{border-top:1px solid #444;padding:13px 0 9px}}
.marker{{font-size:10px;letter-spacing:.11em;color:#aea89f}}
.metric strong{{display:block;font:400 35px Georgia,serif;margin:8px 0 0}}
.metric span{{font:12px ui-monospace,SFMono-Regular,Menlo,monospace;color:#c3b9a7}}
.layout{{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(0,1fr);gap:32px}}
h2{{font-size:22px;font-weight:400;margin:5px 0 12px;letter-spacing:-.02em}}
table{{width:100%;border-collapse:collapse}}
th,td{{border-bottom:1px solid #303030;padding:10px 9px;text-align:left;vertical-align:top;font-size:12px}}
th{{color:#a9a39a;font-size:10px;letter-spacing:.08em;font-weight:400}}
td.n{{white-space:nowrap;color:#dbb679}}tbody tr:last-child td{{border-bottom:none}}
.issue{{display:block;margin:0 0 5px}}.issue strong{{font-weight:400;color:#f4f0e8}}
.issue small{{display:block;color:#b6b0a7;font-size:10px;margin:3px 0}}
.muted{{color:#8e8981;font-style:italic}}
.key{{border:1px solid #393939;padding:15px 16px;margin-bottom:14px}}
.key label{{display:block;color:#dbb679;font:10px ui-monospace,monospace;letter-spacing:.07em;margin-bottom:8px}}
.key strong{{display:block;font:13px ui-monospace,monospace;font-weight:400;overflow-wrap:anywhere;line-height:1.55}}
.key p{{color:#aba59c;font-size:12px;line-height:1.5;margin:8px 0 0}}
ul{{padding-left:18px;margin:12px 0}}li{{color:#c2bdb4;font-size:13px;line-height:1.58;margin-bottom:6px}}
.meta{{font-size:10px;color:#8c8781;border-top:1px solid #393939;margin-top:23px;padding-top:13px;line-height:1.8;overflow-wrap:anywhere}}
@media(max-width:900px){{main{{padding:24px}}.metrics{{grid-template-columns:repeat(2,1fr)}}.layout{{grid-template-columns:1fr}}h1{{font-size:34px}}}}
</style></head><body><main>
<div class="eyebrow">SMART-X / PROG7312 / P2-06 / AUTHENTIC LOCAL API EVIDENCE</div>
<h1>One active condition. No duplicate work.</h1>
<p class="lead">A real <code>HashSet&lt;string&gt;</code> controls active membership. Repeated reports update the same issue; acknowledgement does not clear it. Recovery removes the membership and recurrence starts a new episode. Data is taken from the actual Part 1 and Part 2 APIs.</p>
<div class="metrics">
<div class="metric"><div class="marker">SPIKE REPORTS IN FIRST EPISODE</div><strong>3 → 1</strong><span>Three reports, one issue ID</span></div>
<div class="metric"><div class="marker">SEVERITY PROGRESSION</div><strong>W → C</strong><span>Warning to Critical</span></div>
<div class="metric"><div class="marker">CONFIRMED RECOVERY</div><strong>2</strong><span>Consecutive stable readings</span></div>
<div class="metric"><div class="marker">PROCESSED FLOAT EVENTS</div><strong>{escape(prior)}</strong><span>P2-05 / all typed values retained</span></div>
</div>
<hr class="line">
<div class="layout"><section>
<div class="eyebrow">01 / ACTUAL CONDITION LIFECYCLE</div>
<h2>Membership through live operations</h2>
<table><thead><tr><th>#</th><th>API EVENT</th><th>ACTIVE</th><th>NEW</th><th>ISSUES AND OCCURRENCES</th></tr></thead><tbody>
{rows}
</tbody></table>
</section><section>
<div class="eyebrow">02 / SET IDENTITY</div><h2>Stable key, distinct episodes</h2>
<div class="key"><label>SENSOR + CONDITION MEMBERSHIP KEY</label>
<strong>{escape(stable_key)}</strong><p>This key persists across fault reports, acknowledgement, recovery, and later recurrence.</p></div>
<div class="key"><label>ORIGINAL ISSUE ID</label><strong>{escape(original)}</strong><p>Occurrence count reached three without duplicating the original active issue.</p></div>
<div class="key"><label>NEW ISSUE ID AFTER RECURRENCE</label><strong>{escape(recurrence)}</strong><p>After two stable readings and recovery, a later spike produced a new episode.</p></div>
<div class="eyebrow">03 / CROSS-FEATURE INVARIANTS</div>
<ul>{verification}</ul>
</section></div>
<div class="meta">CAPTURED {escape(data['captureTimestampUtc'])} | BASE URL {escape(data['localApiBaseUrl'])}<br>
PRIMARY SENSOR {escape(primary_id)} | SECONDARY SENSOR {escape(secondary_id)}<br>
SOURCE: validated HTTP results in adjacent .json. This is a rendered evidence report, not a screenshot of the future P2-07 React workspace. Data stores are process-local.</div>
</main></body></html>'''


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--output-dir", default="docs/evidence/part2")
    args = parser.parse_args()

    captured = capture(LocalSmartX(args.base_url))
    # No files are written until *every* live-API invariant has passed.
    out = Path(args.output_dir)
    out.mkdir(parents=True, exist_ok=True)
    (out / "07-unique-active-errors.json").write_text(
        json.dumps(captured, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    (out / "07-unique-active-errors.html").write_text(
        render_html(captured), encoding="utf-8"
    )
    print("VERIFIED: one key per condition; repeated faults preserve one issue ID.")
    print("VERIFIED: warning escalation; acknowledgement without recovery.")
    print("VERIFIED: two stable readings recover; recurrence creates a new issue ID.")
    print("VERIFIED: second sensor isolation; Part 1/P2-06 snapshots agree.")
    print("VERIFIED: seven original typed readings survive Part 1 and P2-05.")
    print("Generated 07-unique-active-errors.json and .html. Render the PNG next.")


if __name__ == "__main__":
    try:
        main()
    except (EvidenceError, ValueError, KeyError, TypeError, OSError) as error:
        print("Evidence capture failed: " + str(error), file=sys.stderr)
        raise SystemExit(1) from error
