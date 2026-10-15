#!/usr/bin/env python3
"""Record genuine Smart-X P2-05 history, ordering and Undo evidence via local HTTP."""

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


class LocalApi:
    """Minimal local-only HTTP client; no extra Python dependencies or proxy."""

    def __init__(self, base_url):
        parsed = urllib.parse.urlparse(base_url)
        if (
            parsed.scheme not in ("http", "https")
            or parsed.hostname not in ("localhost", "127.0.0.1", "::1")
            or parsed.path not in ("", "/")
            or parsed.query
            or parsed.fragment
            or parsed.username
            or parsed.password
        ):
            raise ValueError("Use a local API base URL, e.g. http://localhost:8080")
        self.base_url = base_url.rstrip("/")
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
                f"Local Smart-X API is unreachable: {error.reason}"
            ) from error
        try:
            content = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise RuntimeError(
                f"{method} {path} returned non-JSON HTTP {status}"
            ) from error
        if status != expected:
            raise RuntimeError(
                f"{method} {path}: expected HTTP {expected}, got HTTP {status}: {content}"
            )
        return content

    def processing(self):
        state = self.request("/api/realtime/processing")
        require(
            state["failure"] is None
            and state["workerStatus"] not in ("Faulted", "Stopped"),
            "Telemetry worker is not healthy",
        )
        require(
            state["acceptedCount"]
            == state["pendingCount"]
            + state["processedCount"]
            + state["outstandingDispatchedCount"],
            "P2-03 accepted-work accounting is inconsistent",
        )
        require(
            state["dispatchedCount"]
            == state["processedCount"] + state["outstandingDispatchedCount"],
            "P2-03 dispatched-work accounting is inconsistent",
        )
        return state

    def pause(self, paused):
        return self.request(
            "/api/realtime/simulation/ordinary-dispatch", "PUT", {"paused": paused}
        )

    def history(self, sensor_id, query="?limit=500&order=asc"):
        return self.request(f"/api/realtime/devices/{sensor_id}/history{query}")

    def wait_history(self, sensor_id, count, timeout=25):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = self.history(sensor_id)
            if result["totalRecordedEvents"] >= count:
                return result
            self.processing()  # Fail early if the worker faults.
            time.sleep(0.05)
        raise RuntimeError(
            f"Timed out waiting for {count} history events on {sensor_id}"
        )

    def wait_dispatches(self, sensor_id, count, timeout=25):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = self.processing()
            records = [
                record
                for record in state["recentDispatches"]
                if record["telemetry"]["sensorProfileId"] == sensor_id
            ]
            if len(records) == count and all(
                row["outcome"] == "Processed" for row in records
            ):
                return state, records
            time.sleep(0.05)
        raise RuntimeError(f"Timed out waiting for {count} processed dispatch records")


def require(condition, message):
    """Abort the evidence capture rather than writing unverified claims."""
    if not condition:
        raise RuntimeError(message)


def utc(value):
    """Interpret ISO-8601 server values with offsets as one UTC instant."""
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def iso(value):
    return value.astimezone(timezone.utc).isoformat()


def register(api, name, category, telemetry_type):
    unique = f"p205-evidence-{uuid.uuid4().hex}"
    return api.request(
        "/api/sensors",
        "POST",
        {
            "name": name,
            "deviceIdentifier": unique,
            "identifierKind": "UniqueIdentifier",
            "deploymentLocation": {
                "facility": "Evidence Demo",
                "zone": "Sorted History",
                "nodeId": unique,
            },
            "category": category,
            "telemetryDataType": telemetry_type,
        },
        expected=201,
    )


def ingest(api, sensor_id, value, captured_at, boolean=False):
    payload = {"capturedAtUtc": iso(captured_at)}
    if boolean:
        payload["booleanValue"] = value
    else:
        payload["integerValue"] = value
    return api.request(
        f"/api/sensors/{sensor_id}/telemetry", "POST", payload, expected=201
    )


def target_device(api, sensor_id):
    snapshot = api.request("/api/realtime/snapshot")
    matches = [item for item in snapshot["devices"] if item["id"] == sensor_id]
    require(
        len(matches) == 1, "Expected exactly one registered sensor in realtime snapshot"
    )
    return matches[0]


def capture(api):
    """Verify real acceptance, dispatch, chronology and mixed actuator history."""
    baseline = api.processing()
    require(
        baseline["pendingCount"] == 0 and baseline["outstandingDispatchedCount"] == 0,
        "Run the evidence on a quiet local API with no outstanding work",
    )
    original_pause = baseline["ordinaryDispatchPaused"]
    try:
        api.pause(True)
        integer = register(api, "P2-05 Integer Timeline", "Environmental", "Integer")
        integer_id = integer["id"]
        base = datetime.now(timezone.utc) - timedelta(minutes=3)
        events = [
            ("A", 10, base + timedelta(seconds=10)),
            ("B", 11, base + timedelta(seconds=30)),
            ("C", 100, base + timedelta(seconds=30)),
            ("D", 12, base),  # Backfilled event arrives fourth but occurred first.
        ]
        accepted = []
        for label, value, captured in events[:2]:
            accepted.append(ingest(api, integer_id, value, captured))
        pending = api.processing()
        require(
            pending["ordinaryPendingCount"] == 2
            and pending["criticalPendingCount"] == 0,
            "A and B were not both pending ordinary work",
        )
        require(
            pending["dispatchedCount"] == baseline["dispatchedCount"],
            "Ordinary work dispatched despite the pause setting",
        )
        require(
            api.history(integer_id)["totalRecordedEvents"] == 0,
            "Accepted, unprocessed work appeared in chronological history",
        )

        # C has the same capture instant as B. This is important: the
        # anomaly classifier intentionally does not classify older backfills
        # as spikes. The 11 -> 100 observation enters the critical lane.
        accepted.append(ingest(api, integer_id, events[2][1], events[2][2]))
        critical_history = api.wait_history(integer_id, 1)
        critical = critical_history["events"]
        require(
            len(critical) == 1 and critical[0]["telemetry"]["integerValue"] == 100,
            "C did not become the first processed history entry",
        )
        require(
            critical[0]["processingLane"] == "Critical"
            and critical[0]["severity"] == "Critical",
            "C was not classified critical",
        )
        bypass = api.processing()
        require(
            bypass["ordinaryDispatchPaused"] and bypass["ordinaryPendingCount"] == 2,
            "C did not bypass A/B while ordinary dispatch was paused",
        )

        # D is a genuine backfill accepted after C. Being older than C,
        # it stays in the ordinary queue and later sorts FIRST by capture time.
        accepted.append(ingest(api, integer_id, events[3][1], events[3][2]))
        paused_with_backfill = api.processing()
        require(
            paused_with_backfill["ordinaryPendingCount"] == 3,
            "Backfilled D should be pending alongside A/B",
        )
        require(
            api.history(integer_id)["totalRecordedEvents"] == 1,
            "Unprocessed backfill was admitted to operational history",
        )
        api.pause(False)
        integer_history = api.wait_history(integer_id, 4)
        completed, dispatches = api.wait_dispatches(integer_id, 4)
        part_one = api.request(f"/api/sensors/{integer_id}/telemetry?limit=100")
        require(len(part_one) == 4, "Part 1 did not retain all accepted readings")
        require(
            [r["integerValue"] for r in part_one] == [10, 11, 100, 12],
            "Part 1 ingestion order changed",
        )
        require(
            [r["integerValue"] for r in accepted] == [10, 11, 100, 12],
            "Unexpected acceptance response values",
        )
        require(
            [r["telemetry"]["integerValue"] for r in dispatches] == [100, 10, 11, 12],
            "P2-03 dispatch order was not C -> A -> B -> D",
        )
        require(
            [r["lane"] for r in dispatches]
            == ["Critical", "Ordinary", "Ordinary", "Ordinary"],
            "Unexpected priority/FIFO lanes",
        )
        history_events = integer_history["events"]
        require(
            len(history_events) == 4
            and integer_history["totalRecordedEvents"] == 4
            and integer_history["retainedEventCount"] == 4,
            "Sorted history did not retain all four processed readings",
        )
        require(
            [r["telemetry"]["integerValue"] for r in history_events]
            == [12, 10, 100, 11],
            "SortedDictionary ordering is not D -> A -> C -> B",
        )
        require(
            [r["sequence"] for r in history_events] == [4, 2, 1, 3],
            "History did not retain stable worker insertion sequences",
        )
        require(
            [r["enqueueSequence"] for r in history_events]
            == [
                dispatches[3]["enqueueSequence"],
                dispatches[1]["enqueueSequence"],
                dispatches[0]["enqueueSequence"],
                dispatches[2]["enqueueSequence"],
            ],
            "Source enqueue sequences were lost",
        )
        require(
            utc(history_events[2]["occurredAtUtc"])
            == utc(history_events[3]["occurredAtUtc"]),
            "Equal-timestamp B/C collision was not preserved",
        )
        require(
            history_events[2]["eventId"] != history_events[3]["eventId"],
            "Equal-time events overwrote one another",
        )
        require(
            len({r["eventId"] for r in history_events}) == 4,
            "Timeline contains duplicate event IDs",
        )
        for entry in history_events:
            require(
                entry["kind"] == "TelemetryProcessed" and entry["command"] is None,
                "Integer device history contains an unexpected event type",
            )
        limited = api.history(integer_id, "?limit=2&order=desc")
        require(
            [r["telemetry"]["integerValue"] for r in limited["events"]] == [11, 100],
            "Descending bounded query did not select the newest two occurrences",
        )
        integer_device = target_device(api, integer_id)
        require(
            integer_device["latestProcessedTelemetry"]["integerValue"] == 100,
            "Latest processed observation was overwritten by older work",
        )
        require(integer_device["status"] == "Online", "Unexpected operational status")
        require(completed["failure"] is None, "Worker faulted during capture")

        # A second device demonstrates that actual command and Undo events
        # share the SAME SortedDictionary timeline as processed telemetry.
        actuator = register(api, "P2-05 Mixed Actuator Timeline", "Actuator", "Boolean")
        actuator_id = actuator["id"]
        api.request(f"/api/sensors/{actuator_id}/status", "PATCH", {"status": "Online"})
        boolean_capture = datetime.now(timezone.utc) - timedelta(seconds=30)
        reported = ingest(api, actuator_id, False, boolean_capture, boolean=True)
        api.wait_history(actuator_id, 1)
        command_path = f"/api/realtime/devices/{actuator_id}/commands"
        relay_on = api.request(command_path, "POST", {"commandType": "RelayOn"})
        no_op = api.request(command_path, "POST", {"commandType": "RelayOn"})
        require(
            relay_on["command"]["stateChanged"] is True
            and relay_on["actuator"]["undoDepth"] == 1,
            "RelayOn was not undoable",
        )
        require(
            no_op["command"]["stateChanged"] is False
            and no_op["actuator"]["undoDepth"] == 1,
            "No-op consumed an Undo Stack entry",
        )
        original_id = relay_on["command"]["commandId"]
        undo = api.request(
            command_path + "/undo", "POST", {"expectedLatestCommandId": original_id}
        )
        require(
            undo["command"]["isReversal"] is True
            and undo["command"]["reversesCommandId"] == original_id,
            "Undo did not link to the original command",
        )
        require(
            undo["actuator"]["commandedState"] == "Off"
            and undo["actuator"]["undoDepth"] == 0,
            "Undo did not restore Off or pop Stack",
        )
        actuator_history = api.history(actuator_id)
        kinds = [row["kind"] for row in actuator_history["events"]]
        require(
            kinds
            == ["TelemetryProcessed", "RelayCommand", "RelayCommand", "RelayUndo"],
            "Typed telemetry and commands were not recorded in the same timeline",
        )
        require(
            actuator_history["totalRecordedEvents"] == 4
            and [row["sequence"] for row in actuator_history["events"]] == [1, 2, 3, 4],
            "Mixed timeline has missing or reordered events",
        )
        require(
            actuator_history["events"][0]["telemetry"]["booleanValue"] is False,
            "Actual Boolean telemetry was not retained",
        )
        require(
            actuator_history["events"][-1]["command"]["reversesCommandId"]
            == original_id,
            "Chronological history lost reversal linkage",
        )
        command_audit = api.request(command_path)
        require(
            command_audit["totalAuditCount"] == 3
            and command_audit["actuator"]["undoDepth"] == 0,
            "P2-04 command audit or Undo Stack was modified by history projection",
        )
        rejected = api.request(
            command_path + "/undo",
            "POST",
            {"expectedLatestCommandId": original_id},
            expected=409,
        )
        require(rejected["status"] == 409, "Stale Undo was not HTTP 409 ProblemDetails")
        require(
            api.history(actuator_id) == actuator_history,
            "Rejected Undo mutated the operational timeline",
        )
        actuator_device = target_device(api, actuator_id)
        require(
            actuator_device["status"] == "Online"
            and actuator_device["commandedActuator"]["commandedState"] == "Off"
            and actuator_device["latestProcessedTelemetry"]["booleanValue"] is False,
            "Operational status, commanded state or reported telemetry was corrupted",
        )
        part_one_boolean = api.request(
            f"/api/sensors/{actuator_id}/telemetry?limit=100"
        )
        require(
            len(part_one_boolean) == 1 and part_one_boolean[0]["booleanValue"] is False,
            "Commands fabricated or changed Part 1 telemetry",
        )

        return {
            "recordedAtUtc": iso(datetime.now(timezone.utc)),
            "apiBaseUrl": api.base_url,
            "integerSensor": integer,
            "integerFlow": {
                "accepted": accepted,
                "baselineProcessing": baseline,
                "ordinaryPending": pending,
                "criticalCompleted": critical_history,
                "criticalBypassProcessing": bypass,
                "backfillPendingProcessing": paused_with_backfill,
                "completedProcessing": completed,
                "actualDispatches": dispatches,
                "partOneIngestionHistory": part_one,
                "chronologicalHistory": integer_history,
                "boundedDescendingQuery": limited,
                "deviceSnapshot": integer_device,
                "labels": {
                    "ingestion": ["A", "B", "C", "D"],
                    "processing": ["C", "A", "B", "D"],
                    "chronological": ["D", "A", "C", "B"],
                },
            },
            "actuatorSensor": actuator,
            "actuatorFlow": {
                "acceptedBooleanTelemetry": reported,
                "relayOn": relay_on,
                "acceptedNoOp": no_op,
                "undo": undo,
                "history": actuator_history,
                "commandAudit": command_audit,
                "staleUndoProblemDetails": rejected,
                "deviceSnapshot": actuator_device,
                "partOneRecentTelemetry": part_one_boolean,
            },
            "verification": [
                "Four actual Integer telemetry readings accepted in A/B/C/D order",
                "Critical C bypassed pending ordinary A/B; real worker processed C/A/B/D",
                "Backfilled D appears first chronologically: D/A/C/B",
                "B and C share one occurrence timestamp; both retained with stable sequences",
                "Original Part 1 ingestion order remains A/B/C/D",
                "Latest processed observation remains C=100 despite later older work",
                "Actuator history includes real Boolean telemetry, RelayOn, no-op and linked Undo",
                "Undo restores Off; a repeated stale Undo returns HTTP 409 without mutation",
                "Device registration, operational status, Undo Stack and Part 1 telemetry remain independent",
            ],
        }
    finally:
        # This is global simulation state. Restore it even after failed capture.
        api.pause(original_pause)


def esc(value):
    return html.escape(str(value), quote=True)


def clock(value):
    return utc(value).strftime("%H:%M:%S.%f")[:-3] + "Z"


def short(value):
    return esc(str(value)[:8]) if value else "-"


def report(data):
    """Create a standalone, high-contrast report from verified server JSON."""
    integer = data["integerFlow"]
    actor = data["actuatorFlow"]
    map_value = {10: "A", 11: "B", 100: "C", 12: "D"}
    maps = [
        (
            "PART 1",
            "INGESTION",
            "A  /  B  /  C  /  D",
            "Original order of accepted readings",
        ),
        (
            "P2-03",
            "PROCESSING",
            "C  /  A  /  B  /  D",
            "Critical-first, then ordinary FIFO",
        ),
        (
            "P2-05",
            "CHRONOLOGY",
            "D  /  A  /  C  /  B",
            "Sorted timestamp keys; stable ties",
        ),
    ]
    order_cards = "".join(
        '<div class="order"><div class="mono eyebrow">{}</div><div class="order-name">{}</div>'
        '<strong class="mono">{}</strong><p>{}</p></div>'.format(
            *(esc(item) for item in row)
        )
        for row in maps
    )
    integer_rows = "".join(
        '<tr class="{}"><td class="mono">{}</td><td class="mono">{}</td>'
        '<td>{}</td><td class="mono">{}</td><td class="mono">{}</td><td class="mono">{}</td></tr>'.format(
            "highlight" if row["telemetry"]["integerValue"] in (11, 100) else "",
            esc(map_value[row["telemetry"]["integerValue"]]),
            esc(row["telemetry"]["integerValue"]),
            esc(clock(row["occurredAtUtc"])),
            esc(row["processingLane"]),
            esc(row["enqueueSequence"]),
            esc(row["sequence"]),
        )
        for row in integer["chronologicalHistory"]["events"]
    )
    actor_rows = []
    for row in actor["history"]["events"]:
        command = row["command"]
        if command is None:
            event = "TELEMETRY"
            transition = "Reported Boolean: false"
            linked = "-"
        else:
            event = (
                "UNDO"
                if command["isReversal"]
                else ("NO-OP" if not command["stateChanged"] else "COMMAND")
            )
            transition = f'{command["commandType"]}: {command["previousState"]} -> {command["resultingState"]}'
            linked = short(command["reversesCommandId"])
        actor_rows.append(
            '<tr><td class="mono">{}</td><td>{}</td><td>{}</td><td class="mono">{}</td></tr>'.format(
                esc(row["sequence"]), esc(event), esc(transition), linked
            )
        )
    actor_table = "".join(actor_rows)
    total = integer["chronologicalHistory"]["totalRecordedEvents"]
    actor_total = actor["history"]["totalRecordedEvents"]
    integer_id = data["integerSensor"]["id"]
    actor_id = data["actuatorSensor"]["id"]
    return (
        """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Smart-X P2-05 | Verified SortedDictionary chronology</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#0a0a0a;color:#f3f1ec;font:15px Georgia,serif}
main{max-width:1390px;margin:auto;padding:46px 54px 38px}
.mono,th,td,.eyebrow,.kicker,.meta,.label{font-family:ui-monospace,SFMono-Regular,Menlo,monospace}
.kicker{color:#86abed;letter-spacing:.15em;font-size:11px}h1{font-weight:400;font-size:43px;letter-spacing:-.04em;margin:12px 0 8px}
p.lead{font-size:16px;color:#aaa8a2;max-width:900px;line-height:1.5;margin:0 0 26px}
.top-rule{height:1px;background:#363636;margin:20px 0 23px}h2{font-weight:400;font-size:22px;margin:28px 0 12px}
.orders{display:grid;grid-template-columns:repeat(3,1fr);border:1px solid #393939}
.order{padding:18px 20px;min-height:144px;border-right:1px solid #393939}.order:last-child{border:0}
.eyebrow{color:#b4b4b4;font-size:10px;letter-spacing:.1em}.order-name{color:#f1f1f1;font-size:15px;margin:5px 0 12px}
.order strong{display:block;color:#86abed;font-size:20px;font-weight:500;letter-spacing:.03em}.order p{color:#999;font-size:12px;margin:9px 0 0}
.metric-row{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin:22px 0 0}
.metric{padding:10px 0 13px;border-top:1px solid #414141}.metric .label{display:block;color:#aaa;font-size:10px;letter-spacing:.08em}
.metric strong{display:block;font-size:27px;font-weight:400;margin:6px 0 0}
.two-col{display:grid;grid-template-columns:1.05fr .95fr;gap:34px}
.section-label{font-size:11px;letter-spacing:.1em;color:#86abed}
table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #333;padding:11px 8px;text-align:left;font-size:12px;white-space:nowrap}
th{color:#aaa;font-size:10px;letter-spacing:.04em;font-weight:400}td{color:#e9e9e9}
tr.highlight td:first-child{color:#86abed}tbody tr:last-child td{border-bottom:0}
.note{margin-top:19px;padding:14px 17px;border-left:2px solid #86abed;background:#121212;color:#c3c3c3;line-height:1.5;font-size:13px}
.meta{margin-top:25px;padding-top:15px;border-top:1px solid #343434;color:#8f8f8f;font-size:10px;line-height:1.9;overflow-wrap:anywhere}
@media(max-width:850px){main{padding:24px}.orders,.metric-row,.two-col{grid-template-columns:1fr}.order{border-right:0;border-bottom:1px solid #393939}h1{font-size:34px}}
</style></head><body><main>
<div class="kicker">SMART-X / PROG7312 / PART 2 / P2-05 / VERIFIED LOCAL API EVIDENCE</div>
<h1>One system. Three correct orders.</h1>
<p class="lead">The same four actual packets were ingested, dispatched and displayed in different legitimate orders. A real per-device SortedDictionary sorts occurrence timestamps and retains colliding events, while Part 1 persistence and P2-03 worker behaviour remain unchanged.</p>
<div class="orders">"""
        + order_cards
        + """</div>
<div class="metric-row">
<div class="metric"><span class="label">INTEGER EVENTS RETAINED</span><strong>"""
        + esc(total)
        + """ / 4</strong></div>
<div class="metric"><span class="label">EQUAL-TIME COLLISION</span><strong>B + C</strong></div>
<div class="metric"><span class="label">LATEST PROCESSED VALUE</span><strong>100 / C</strong></div>
<div class="metric"><span class="label">ACTUATOR EVENTS RETAINED</span><strong>"""
        + esc(actor_total)
        + """ / 4</strong></div>
</div><div class="top-rule"></div>
<div class="two-col"><section><div class="section-label mono">01 / INTEGER SENSOR</div><h2>Sorted capture-time timeline</h2>
<table><thead><tr><th>ID</th><th>VALUE</th><th>OCCURRED UTC</th><th>LANE</th><th>ENQ</th><th>SEQ</th></tr></thead><tbody>"""
        + integer_rows
        + """</tbody></table>
<div class="note">D was accepted last but captured earliest, so it sorts first. B and C share an exact UTC timestamp. C was dispatched first as critical work and remains ahead of B within that timestamp bucket.</div>
</section><section><div class="section-label mono">02 / BOOLEAN ACTUATOR</div><h2>Telemetry + commands + Undo</h2>
<table><thead><tr><th>SEQ</th><th>EVENT</th><th>PAYLOAD / RESTORATION</th><th>REVERSES</th></tr></thead><tbody>"""
        + actor_table
        + """</tbody></table>
<div class="note">RelayOn pushed one Undo entry. A repeated RelayOn was an auditable no-op. Undo restored Off and linked to the original command. A stale repeat returned HTTP 409 without adding history. The one reported Boolean reading stayed false.</div>
</section></div>
<div class="meta">CAPTURED """
        + esc(data["recordedAtUtc"])
        + """ &nbsp; | &nbsp; LOCAL API """
        + esc(data["apiBaseUrl"])
        + """<br>
INTEGER SENSOR """
        + esc(integer_id)
        + """<br>ACTUATOR SENSOR """
        + esc(actor_id)
        + """<br>
SOURCE: verified authoritative HTTP responses retained in adjacent JSON. This HTML/PNG is a rendered evidence report, not a React dashboard screenshot. Operational histories are process-local.</div>
</main></body></html>"""
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--output-dir", default="docs/evidence/part2")
    options = parser.parse_args()

    result = capture(LocalApi(options.base_url))
    output = Path(options.output_dir)
    output.mkdir(parents=True, exist_ok=True)
    base = output / "06-sorted-history-timeline"
    raw_path = base.with_suffix(".json")
    html_path = base.with_suffix(".html")
    raw_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    html_path.write_text(report(result), encoding="utf-8")
    print("Verified ingestion A/B/C/D; processing C/A/B/D; chronology D/A/C/B.")
    print(
        "Verified equal B/C timestamps, backfilled D and genuine command/Undo history."
    )
    print("Verified independent Part 1 telemetry and HTTP 409 stale Undo rejection.")
    print(f"Wrote {raw_path} and {html_path}; render the HTML to a PNG next.")


if __name__ == "__main__":
    try:
        main()
    except (KeyError, TypeError, ValueError, RuntimeError, OSError) as error:
        print("Evidence capture failed: " + str(error), file=sys.stderr)
        sys.exit(1)
