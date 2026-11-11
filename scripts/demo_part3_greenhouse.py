#!/usr/bin/env python3
"""P3-07: Reproduce the IoT greenhouse failure scenario through real HTTP APIs.

This is a LOCAL, in-memory, SIMULATION-ONLY demo. No physical sensor, radio,
relay, or irrigation controller is operated. It registers six test profiles,
creates seven explicitly configured logical links, and deliberately changes
one simulated relay's status. Run it only on an empty local Development API.

Requires only the Python standard library; a companion WebApplicationFactory
xUnit regression implements the same graph and numerical expectations.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlsplit
from urllib.request import Request, urlopen


class DemoFailure(RuntimeError):
    """An API response or routing invariant did not match the scenario."""


@dataclass(frozen=True)
class Device:
    """Registry-generated GUID plus stable human-readable scenario metadata."""

    id: str
    key: str
    name: str


class LocalApi:
    """Minimal JSON HTTP adapter; never modifies server state outside POST/PATCH/DELETE."""

    def __init__(self, base_url: str) -> None:
        parsed = urlsplit(base_url)
        if parsed.scheme not in {"http", "https"}:
            raise DemoFailure("Base URL must use http or https.")
        if parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
            raise DemoFailure("Refusing non-loopback API: this demo is local only.")
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise DemoFailure("Base URL must not include credentials or URL parameters.")
        self.base_url = base_url.rstrip("/") + "/"

    def request(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        expected_status: int = 200,
    ) -> dict[str, Any]:
        """Send a JSON request and reject unexpected status codes or JSON types."""

        url = urljoin(self.base_url, path.lstrip("/"))
        body = json.dumps(payload).encode("utf-8") if payload is not None else None
        headers = {"Accept": "application/json"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        request = Request(url, data=body, headers=headers, method=method)
        try:
            with urlopen(request, timeout=20) as response:
                status = response.status
                raw = response.read()
        except HTTPError as error:
            details = error.read().decode("utf-8", errors="replace")[:1000]
            raise DemoFailure(f"{method} {path}: HTTP {error.code}: {details}") from error
        except URLError as error:
            raise DemoFailure(
                f"Cannot reach local Smart-X API at {self.base_url}: {error.reason}"
            ) from error

        if status != expected_status:
            raise DemoFailure(
                f"{method} {path}: expected HTTP {expected_status}, got {status}"
            )
        if not raw:
            return {}
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError as error:
            raise DemoFailure(f"{method} {path}: response is not JSON") from error
        if not isinstance(parsed, dict):
            raise DemoFailure(f"{method} {path}: expected one JSON object")
        return parsed

    def get(self, path: str) -> dict[str, Any]:
        return self.request("GET", path)

    def post(self, path: str, data: dict[str, Any]) -> dict[str, Any]:
        return self.request("POST", path, data, expected_status=201)

    def patch(self, path: str, data: dict[str, Any]) -> dict[str, Any]:
        return self.request("PATCH", path, data)

    def delete(self, path: str) -> None:
        self.request("DELETE", path, expected_status=204)


def require(condition: bool, message: str) -> None:
    """Fail fast, without printing a misleading 'success' evidence file."""

    if not condition:
        raise DemoFailure(message)


def register(api: LocalApi, suffix: int, name: str, category: str) -> Device:
    """Register through the EXISTING sensors endpoint, then mark Online."""

    created = api.post(
        "/api/sensors",
        {
            "name": name,
            "deviceIdentifier": f"AA:BB:CC:EE:07:{suffix:02X}",
            "identifierKind": "MacAddress",
            "deploymentLocation": {
                "facility": "Greenhouse",
                "zone": "Hydroponics",
                "nodeId": f"Zone-{suffix:02X}",
            },
            "category": category,
            "telemetryDataType": "Float",
        },
    )
    sensor_id = str(created["id"])
    api.patch(f"/api/sensors/{sensor_id}/status", {"status": "Online"})
    return Device(sensor_id, f"sensor/{sensor_id}", name)


def link(api: LocalApi, first: str, second: str, weight: float) -> dict[str, Any]:
    """Only the server is permitted to assign the canonical link UUID."""

    return api.post(
        "/api/topology/links",
        {"nodeAKey": first, "nodeBKey": second, "simulatedEnergyCost": weight},
    )


def snapshot(api: LocalApi, device: Device) -> dict[str, Any]:
    """Read graph/BFS/MST/route independently; require one coherent revision."""

    topology = api.get("/api/topology")
    reachability = api.get("/api/topology/reachability")
    mst = api.get("/api/topology/mst")
    route = api.get(f"/api/topology/devices/{device.id}/route")
    revision = topology["graphVersion"]
    require(
        all(item["graphVersion"] == revision for item in (reachability, mst, route)),
        "Graph changed between reads; stop other writers and rerun on a fresh API.",
    )
    return {
        "graphVersion": revision,
        "nodeCount": topology["nodeCount"],
        "linkCount": topology["linkCount"],
        "registered": reachability["registeredSensorCount"],
        "reachable": reachability["reachableSensorCount"],
        "disconnected": reachability["disconnectedSensorCount"],
        "unavailable": reachability["unavailableSensorCount"],
        "disconnectedComponents": reachability["disconnectedComponents"],
        "selectedEdgeCount": mst["selectedEdgeCount"],
        "backboneCost": mst["totalSimulatedEnergyCost"],
        "routeStatus": route["status"],
        "bfsPath": route["bfs"]["pathToGateway"],
        "bfsHops": route["bfs"]["hopCount"],
        "mstPath": route["mst"]["pathToGateway"],
        "mstPathCost": route["mst"]["accumulatedSimulatedEnergyCost"],
    }


def assert_phase(
    label: str,
    result: dict[str, Any],
    *,
    reachable: int,
    disconnected: int,
    unavailable: int,
    cost: float,
    path: list[str],
) -> None:
    """Check hard expected values and print one concise evidence line."""

    for field, expected in (
        ("reachable", reachable),
        ("disconnected", disconnected),
        ("unavailable", unavailable),
        ("backboneCost", cost),
        ("mstPath", path),
    ):
        require(result[field] == expected, f"{label}: {field}: {result[field]} != {expected}")
    print(
        f"{label:19} v{result['graphVersion']:>3} "
        f"links={result['linkCount']} reachable={reachable} "
        f"disconnected={disconnected} unavailable={unavailable} "
        f"MST={cost} moisture={result['routeStatus']}"
    )


def run(api: LocalApi) -> dict[str, Any]:
    """Create the fixture and verify baseline, failover, cut, and recovery."""

    current = api.get("/api/topology")
    require(
        current["nodeCount"] == 1 and current["linkCount"] == 0,
        "The demo requires an EMPTY process-local mesh. Restart a local "
        "Development API before running; existing sensors are not modified.",
    )

    devices = {
        "primary": register(api, 0xA1, "Primary irrigation relay", "Actuator"),
        "backup": register(api, 0xA2, "Backup irrigation relay", "Actuator"),
        "moisture": register(api, 0xA3, "West-zone moisture probe", "Environmental"),
        "repeater": register(api, 0xA4, "Central greenhouse repeater", "Actuator"),
        "nutrient": register(api, 0xA5, "East-zone nutrient probe", "Environmental"),
        "vulnerable": register(api, 0xA6, "Single-bridge moisture probe", "Environmental"),
    }
    p, b, m, r, n, v = (
        devices[key] for key in
        ("primary", "backup", "moisture", "repeater", "nutrient", "vulnerable")
    )

    link(api, "gateway", p.key, 1.0)
    backup_link = link(api, "gateway", b.key, 5.0)
    link(api, p.key, m.key, 1.0)
    link(api, m.key, r.key, 1.0)
    link(api, r.key, b.key, 1.0)
    link(api, b.key, n.key, 1.25)
    link(api, p.key, v.key, 0.5)

    evidence: dict[str, Any] = {"scenario": "P3-07-greenhouse", "result": "pass"}
    evidence["sensorIds"] = {name: device.id for name, device in devices.items()}

    baseline = snapshot(api, m)
    require(baseline["nodeCount"] == 7 and baseline["linkCount"] == 7,
            "Baseline does not contain six sensors and seven links.")
    assert_phase("baseline", baseline, reachable=6, disconnected=0,
                 unavailable=0, cost=5.75, path=[m.key, p.key, "gateway"])
    require(snapshot(api, n)["mstPath"] ==
            [n.key, b.key, r.key, m.key, p.key, "gateway"],
            "Expected MST path to differ from the nutrient sensor's BFS path.")
    evidence["baseline"] = baseline

    api.patch(f"/api/sensors/{p.id}/status", {"status": "Offline"})
    failed = snapshot(api, m)
    assert_phase("primary offline", failed, reachable=4, disconnected=1,
                 unavailable=1, cost=8.25,
                 path=[m.key, r.key, b.key, "gateway"])
    require(snapshot(api, v)["routeStatus"] == "Disconnected",
            "Vulnerable sensor must be explicitly disconnected.")
    require(failed["graphVersion"] > baseline["graphVersion"],
            "Failure did not advance the graph revision.")
    evidence["primaryOffline"] = failed

    api.patch(f"/api/sensors/{p.id}/status", {"status": "Online"})
    recovered = snapshot(api, m)
    assert_phase("primary recovered", recovered, reachable=6, disconnected=0,
                 unavailable=0, cost=5.75, path=[m.key, p.key, "gateway"])
    evidence["primaryRecovered"] = recovered

    api.delete(f"/api/topology/links/{backup_link['id']}")
    api.patch(f"/api/sensors/{p.id}/status", {"status": "Offline"})
    isolated = snapshot(api, m)
    assert_phase("no gateway bridge", isolated, reachable=0, disconnected=5,
                 unavailable=1, cost=0.0, path=[])
    require(len(isolated["disconnectedComponents"]) == 2,
            "Expected a four-sensor island and a single vulnerable sensor.")
    evidence["noGatewayBridge"] = isolated

    restored_backup = link(api, "gateway", b.key, 5.0)
    require(restored_backup["id"] != backup_link["id"],
            "A recreated simulated link must receive a new ID.")
    rerouted = snapshot(api, m)
    assert_phase("backup restored", rerouted, reachable=4, disconnected=1,
                 unavailable=1, cost=8.25,
                 path=[m.key, r.key, b.key, "gateway"])
    require(snapshot(api, v)["routeStatus"] == "Disconnected",
            "Restoring backup must not invent a connection to the vulnerable sensor.")
    evidence["backupRestored"] = rerouted
    return evidence


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--base-url", default="http://localhost:5203",
        help="Loopback-only Development API; default http://localhost:5203",
    )
    parser.add_argument(
        "--confirm-simulation", action="store_true",
        help="Required: acknowledge that this creates and mutates local demo data",
    )
    parser.add_argument(
        "--evidence", type=Path,
        help="Optional path to write the completed, checked JSON evidence",
    )
    args = parser.parse_args()
    if not args.confirm_simulation:
        parser.error("Pass --confirm-simulation for intentional local mesh writes.")

    try:
        result = run(LocalApi(args.base_url))
        if args.evidence is not None:
            args.evidence.parent.mkdir(parents=True, exist_ok=True)
            args.evidence.write_text(
                json.dumps(result, indent=2) + "\n", encoding="utf-8"
            )
            print(f"Verified evidence saved to {args.evidence}")
    except (DemoFailure, KeyError, TypeError, ValueError) as error:
        print(f"GREENHOUSE DEMO FAILED: {error}", file=sys.stderr)
        return 1

    print("P3-07 GREENHOUSE DEMO PASSED (real local HTTP requests)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
