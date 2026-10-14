#!/usr/bin/env python3
"""Capture verified P2-04 command and Undo evidence from a local Smart-X API."""

import argparse
import html
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


class LocalApi:
    """Small standard-library HTTP client restricted to local API instances."""

    def __init__(self, base_url):
        parsed = urllib.parse.urlparse(base_url)
        if parsed.scheme not in ("http", "https") or parsed.hostname not in (
            "localhost",
            "127.0.0.1",
            "::1",
        ):
            raise ValueError("Use a local API, e.g. http://localhost:8080.")
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError("The base URL must not include credentials or query data.")
        self.base_url = base_url.rstrip("/")
        # Evidence should never silently use an environment HTTP proxy.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(self, path, method="GET", body=None, expected=200):
        payload = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            self.base_url + path,
            data=payload,
            method=method,
            headers={"Accept": "application/json", "Content-Type": "application/json"},
        )
        try:
            with self.opener.open(request, timeout=15) as response:
                status = response.status
                raw = response.read()
        except urllib.error.HTTPError as error:
            status = error.code
            raw = error.read()
        except urllib.error.URLError as error:
            raise RuntimeError(
                f"Cannot reach local Smart-X API: {error.reason}"
            ) from error

        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise RuntimeError(
                f"{method} {path} did not return JSON (HTTP {status})."
            ) from error

        if status != expected:
            raise RuntimeError(
                f"{method} {path}: expected HTTP {expected}, got {status}: {parsed}"
            )
        return parsed


def require(condition, explanation):
    """Stop capture rather than publishing misleading or incomplete evidence."""
    if not condition:
        raise RuntimeError(explanation)


def device_from_snapshot(api, target_id):
    snapshot = api.request("/api/realtime/snapshot")
    matches = [device for device in snapshot["devices"] if device["id"] == target_id]
    require(
        len(matches) == 1, "Expected exactly one registered target in the snapshot."
    )
    return matches[0]


def check_state(device, expected_state, expected_depth, expected_latest):
    actuator = device["commandedActuator"]
    require(actuator is not None, "Target is missing commanded actuator metadata.")
    require(actuator["commandedState"] == expected_state, "Incorrect commanded state.")
    require(actuator["undoDepth"] == expected_depth, "Incorrect real Stack depth.")
    require(
        actuator["latestUndoableCommandId"] == expected_latest,
        "Incorrect latest Undo Stack command ID.",
    )
    require(device["status"] == "Online", "Command changed sensor operational status.")
    require(device["latestProcessedTelemetry"] is None, "Command fabricated telemetry.")


def check_history(history, state, depth, latest, count):
    require(history["actuator"]["commandedState"] == state, "History state mismatch.")
    require(history["actuator"]["undoDepth"] == depth, "History Undo depth mismatch.")
    require(
        history["actuator"]["latestUndoableCommandId"] == latest,
        "History latest command mismatch.",
    )
    require(history["totalAuditCount"] == count, "Unexpected command audit count.")
    require(len(history["recentAudit"]) == count, "Unexpected audit records for demo.")


def capture(api):
    """Exercise real HTTP operations and verify every state transition."""
    unique_identifier = "p204-undo-evidence-" + uuid.uuid4().hex
    registered = api.request(
        "/api/sensors",
        method="POST",
        expected=201,
        body={
            "name": "Relay Undo Evidence",
            "deviceIdentifier": unique_identifier,
            "identifierKind": "UniqueIdentifier",
            "deploymentLocation": {
                "facility": "Evidence Demo",
                "zone": "Command Stream",
                "nodeId": "Relay 04",
            },
            "category": "Actuator",
            "telemetryDataType": "Boolean",
        },
    )
    device_id = registered["id"]
    online = api.request(
        f"/api/sensors/{device_id}/status",
        method="PATCH",
        body={"status": "Online"},
    )
    require(online["status"] == "Online", "Actuator did not become Online.")

    command_path = f"/api/realtime/devices/{device_id}/commands"
    history_before = api.request(command_path)
    device_before = device_from_snapshot(api, device_id)
    check_history(history_before, "Off", 0, None, 0)
    check_state(device_before, "Off", 0, None)

    relay_on = api.request(command_path, method="POST", body={"commandType": "RelayOn"})
    original_id = relay_on["command"]["commandId"]
    require(relay_on["command"]["stateChanged"], "RelayOn did not change state.")
    require(not relay_on["command"]["isReversal"], "RelayOn is marked as reversal.")
    require(relay_on["command"]["previousState"] == "Off", "Wrong previous state.")
    require(relay_on["command"]["resultingState"] == "On", "Wrong resulting state.")
    require(relay_on["actuator"]["undoDepth"] == 1, "RelayOn was not pushed.")
    require(
        relay_on["actuator"]["latestUndoableCommandId"] == original_id,
        "RelayOn is not the latest undoable command.",
    )
    device_after_on = device_from_snapshot(api, device_id)
    check_state(device_after_on, "On", 1, original_id)

    # Accepted no-op is audited, but does not push a second Stack entry.
    no_op = api.request(command_path, method="POST", body={"commandType": "RelayOn"})
    require(not no_op["command"]["stateChanged"], "Repeated RelayOn was not a no-op.")
    require(no_op["actuator"]["undoDepth"] == 1, "No-op changed Stack depth.")
    require(
        no_op["actuator"]["latestUndoableCommandId"] == original_id,
        "No-op replaced the latest undoable command.",
    )
    history_after_no_op = api.request(command_path)
    check_history(history_after_no_op, "On", 1, original_id, 2)

    reversed_command = api.request(
        command_path + "/undo",
        method="POST",
        body={"expectedLatestCommandId": original_id},
    )
    reversal = reversed_command["command"]
    require(reversal["isReversal"], "Undo was not recorded as a reversal.")
    require(reversal["stateChanged"], "Undo did not restore the previous state.")
    require(reversal["reversesCommandId"] == original_id, "Reversal link is wrong.")
    require(reversal["commandId"] != original_id, "Reversal reused the original ID.")
    require(reversal["previousState"] == "On", "Undo previous state is wrong.")
    require(reversal["resultingState"] == "Off", "Undo did not restore Off.")
    require(reversed_command["actuator"]["undoDepth"] == 0, "Undo did not pop.")
    require(
        reversed_command["actuator"]["latestUndoableCommandId"] is None,
        "Undo reversal was incorrectly pushed onto the Stack.",
    )

    history_after_undo = api.request(command_path)
    device_after_undo = device_from_snapshot(api, device_id)
    check_history(history_after_undo, "Off", 0, None, 3)
    check_state(device_after_undo, "Off", 0, None)
    audit = history_after_undo["recentAudit"]
    require(
        [entry["commandId"] for entry in audit]
        == [original_id, no_op["command"]["commandId"], reversal["commandId"]],
        "Audit order/identity does not match accepted API operations.",
    )
    require(
        [entry["stateChanged"] for entry in audit] == [True, False, True],
        "Audit state-change flags are wrong.",
    )
    require(audit[-1]["reversesCommandId"] == original_id, "Audit lost reversal link.")

    # A repeated/stale Undo must receive Conflict and change absolutely nothing.
    stale_rejection = api.request(
        command_path + "/undo",
        method="POST",
        body={"expectedLatestCommandId": original_id},
        expected=409,
    )
    require(stale_rejection["status"] == 409, "Stale Undo is not ProblemDetails 409.")
    history_after_rejection = api.request(command_path)
    require(
        history_after_rejection == history_after_undo, "Stale Undo mutated audit/state."
    )
    check_state(device_from_snapshot(api, device_id), "Off", 0, None)

    return {
        "recordedAtUtc": datetime.now(timezone.utc).isoformat(),
        "apiBaseUrl": api.base_url,
        "deviceIdentifier": unique_identifier,
        "deviceId": device_id,
        "registered": registered,
        "onlineProfile": online,
        "before": {"device": device_before, "history": history_before},
        "relayOn": {"response": relay_on, "device": device_after_on},
        "acceptedNoOp": {"response": no_op, "history": history_after_no_op},
        "undo": {"response": reversed_command, "device": device_after_undo},
        "after": {"history": history_after_undo, "device": device_after_undo},
        "staleUndo": {"httpStatus": 409, "problemDetails": stale_rejection},
        "afterStaleUndo": history_after_rejection,
        "verification": [
            "Initial commanded state Off; empty Undo Stack",
            "RelayOn committed Off to On; Stack depth became 1",
            "Repeat RelayOn accepted as no-op; Stack depth stayed 1",
            "Undo restored the recorded Off state; Stack depth became 0",
            "Original, no-op and linked reversal remain in immutable HTTP audit",
            "Stale Undo rejected with HTTP 409 and no state/audit mutation",
            "Operational status stayed Online; no reported telemetry was fabricated",
        ],
    }


def escape(value):
    return html.escape(str(value), quote=True)


def abbreviated(value):
    return escape(str(value)[:8]) if value else "—"


def report(data):
    """Render an evidence sheet from verified, unmodified API responses."""
    before = data["before"]["history"]["actuator"]
    after_on = data["relayOn"]["response"]["actuator"]
    after_no_op = data["acceptedNoOp"]["response"]["actuator"]
    after_undo = data["after"]["history"]["actuator"]
    original = data["relayOn"]["response"]["command"]
    reversal = data["undo"]["response"]["command"]
    phases = [
        ("01 / INITIAL", before, "No commands"),
        ("02 / RELAY ON", after_on, "State-changing command"),
        ("03 / NO-OP", after_no_op, "Audit only; no Stack push"),
        ("04 / UNDO", after_undo, "Recorded state restored"),
    ]
    phase_html = "".join(
        '<div class="phase"><div class="phase-label">{}</div>'
        '<strong>{}</strong><div class="depth">UNDO STACK / {}</div>'
        "<p>{}</p></div>".format(
            escape(label),
            escape(state["commandedState"]),
            escape(state["undoDepth"]),
            escape(description),
        )
        for label, state, description in phases
    )
    audit_rows = []
    for record in data["after"]["history"]["recentAudit"]:
        kind = (
            "REVERSAL"
            if record["isReversal"]
            else ("STATE CHANGE" if record["stateChanged"] else "NO-OP")
        )
        audit_rows.append(
            "<tr><td>{}</td><td>{}</td><td>{} → {}</td><td>{}</td><td>{}</td></tr>".format(
                escape(kind),
                escape(record["commandType"]),
                escape(record["previousState"]),
                escape(record["resultingState"]),
                abbreviated(record["commandId"]),
                abbreviated(record["reversesCommandId"]),
            )
        )
    return (
        """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Smart-X P2-04 | Verified command and Undo evidence</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#0a0a0a;color:#f5f4ee;font:15px Georgia,serif}
main{max-width:1340px;margin:0 auto;padding:48px 54px 50px}
.kicker,.meta,.phase-label,.depth,th,td,.tag,.eyebrow,.small{font-family:ui-monospace,SFMono-Regular,Menlo,monospace}
.kicker{font-size:12px;letter-spacing:.16em;color:#93b7ff}
h1{font-weight:400;font-size:45px;letter-spacing:-.04em;margin:13px 0 7px}
.lead{font-size:17px;color:#b8b8b8;margin:0 0 26px;line-height:1.5}
.rule{border-top:1px solid #343434;margin:22px 0 24px}
.eyebrow{font-size:11px;letter-spacing:.13em;color:#ababab;margin-bottom:11px}
.sequence{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));border:1px solid #343434}
.phase{padding:19px 18px 18px;min-height:169px;border-right:1px solid #343434}
.phase:last-child{border-right:0}.phase-label{font-size:11px;letter-spacing:.08em;color:#a7a7a7}
.phase strong{display:block;margin:13px 0 8px;font:400 37px Georgia,serif;letter-spacing:-.03em}
.phase:nth-child(2) strong,.phase:nth-child(3) strong{color:#93b7ff}
.depth{font-size:11px;color:#e6e6e6}.phase p{color:#9c9c9c;font-size:13px;margin:12px 0 0}
.grid{display:grid;grid-template-columns:1.45fr 1fr;gap:28px;margin-top:29px}
h2{font-size:21px;font-weight:400;margin:0 0 13px}
table{border-collapse:collapse;width:100%;font-size:12px}
th,td{border-bottom:1px solid #333;padding:12px 9px;text-align:left;white-space:nowrap}
th{font-size:10px;letter-spacing:.07em;color:#b1b1b1;font-weight:400}
td{color:#eee}tbody tr:last-child td{border-bottom:0}
.detail{border-top:1px solid #373737;padding:14px 0;display:flex;justify-content:space-between;gap:14px}
.detail .label{color:#a8a8a8}.detail .value{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px;text-align:right}
.status{padding:15px 17px;border-left:2px solid #93b7ff;background:#121212;margin-top:21px;color:#d3d3d3;font-size:14px;line-height:1.55}
.meta{border-top:1px solid #343434;margin-top:30px;padding-top:15px;color:#969696;font-size:10px;line-height:1.85;overflow-wrap:anywhere}
</style></head><body><main>
<div class="kicker">SMART-X / PROG7312 / PART 2 / ISSUE P2-04</div>
<h1>Manual relay command. Genuine Undo.</h1>
<p class="lead">Verified API transaction sequence — commanded state is restored, the real per-device Stack is emptied, and both command and reversal remain audited.</p>
<div class="eyebrow">AUTHORITATIVE COMMAND STATE / LIVE API</div>
<div class="sequence">"""
        + phase_html
        + """</div>
<div class="grid"><section><h2>Permanent command audit</h2>
<table><thead><tr><th>EVENT</th><th>COMMAND</th><th>PREVIOUS → RESULT</th><th>COMMAND ID</th><th>REVERSES</th></tr></thead>
<tbody>"""
        + "".join(audit_rows)
        + """</tbody></table>
</section><section><h2>Validated invariants</h2>
<div class="detail"><span class="label">Original command</span><span class="value">"""
        + abbreviated(original["commandId"])
        + """</span></div>
<div class="detail"><span class="label">Undo reversal</span><span class="value">"""
        + abbreviated(reversal["commandId"])
        + """</span></div>
<div class="detail"><span class="label">Reverses original</span><span class="value">"""
        + abbreviated(reversal["reversesCommandId"])
        + """</span></div>
<div class="detail"><span class="label">Final state / Stack</span><span class="value">OFF / 0</span></div>
<div class="detail"><span class="label">Audit records retained</span><span class="value">3 / 3</span></div>
<div class="detail"><span class="label">Stale Undo</span><span class="value">HTTP 409 — NO MUTATION</span></div>
<div class="detail"><span class="label">Operational status</span><span class="value">ONLINE (UNCHANGED)</span></div>
</section></div>
<div class="status">The original RelayOn was pushed once. A repeated RelayOn was recorded as a no-op without pushing. Undo restored the original Off state and popped that original entry. A second Undo using the stale ID returned 409; audit, Stack, and state stayed unchanged.</div>
<div class="meta">RECORDED AT """
        + escape(data["recordedAtUtc"])
        + """<br>
API """
        + escape(data["apiBaseUrl"])
        + """<br>DEVICE """
        + escape(data["deviceId"])
        + """<br>
Evidence is rendered from validated local HTTP responses; raw before/after requests and results are preserved in the adjacent JSON file. This is an API evidence report, not a screenshot of a completed React dashboard.</div>
</main></body></html>"""
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--output-dir", default="docs/evidence/part2")
    args = parser.parse_args()

    data = capture(LocalApi(args.base_url))
    destination = Path(args.output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    stem = destination / "04-command-history-undo"
    json_file = stem.with_suffix(".json")
    html_file = stem.with_suffix(".html")
    json_file.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    html_file.write_text(report(data), encoding="utf-8")
    print("Verified Off -> On -> accepted no-op -> Undo Off; stale Undo = HTTP 409.")
    print("Verified audit linkage, Stack depth and unchanged operational status.")
    print(f"Wrote {json_file} and {html_file}")
    print("Capture 04-command-history-undo.png from the HTML report next.")


if __name__ == "__main__":
    try:
        main()
    except (KeyError, ValueError, RuntimeError, OSError, TypeError) as error:
        print("Evidence capture failed: " + str(error), file=sys.stderr)
        sys.exit(1)
