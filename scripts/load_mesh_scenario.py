#!/usr/bin/env python3
"""Deterministic local greenhouse mesh demo through Smart-X's existing HTTP API.

Requires Python 3.10+, no pip packages, no cloud accounts, no API keys.
Safe by default: only accepts a fresh graph or its exact known demo sensors
and links; refuses arbitrary existing topology to protect operator work.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


@dataclass(frozen=True)
class Device:
    suffix: int
    name: str
    category: str


DEVICES = {
    "primary": Device(0xA1, "Primary irrigation relay", "Actuator"),
    "backup": Device(0xA2, "Backup irrigation relay", "Actuator"),
    "moisture": Device(0xA3, "West-zone moisture probe", "Environmental"),
    "repeater": Device(0xA4, "Central greenhouse repeater", "Actuator"),
    "nutrient": Device(0xA5, "East-zone nutrient probe", "Environmental"),
    "vulnerable": Device(0xA6, "Single-bridge moisture probe", "Environmental"),
}

# Same seven links and positive simulated costs as the ASP.NET integration test.
EDGES = (
    ("gateway", "primary", 1.0),
    ("gateway", "backup", 5.0),
    ("primary", "moisture", 1.0),
    ("moisture", "repeater", 1.0),
    ("repeater", "backup", 1.0),
    ("backup", "nutrient", 1.25),
    ("primary", "vulnerable", 0.5),
)

EXPECTED = {
    "healthy": (6, 0, 0, 5.75),
    "relay-failure": (4, 1, 1, 8.25),
    "island": (0, 5, 1, 0.0),
    "recovered": (6, 0, 0, 5.75),
}


class ScenarioError(Exception):
    """Expected error reported without a Python traceback."""


class Api:
    def __init__(self, base: str) -> None:
        parsed = urlsplit(base)
        if parsed.scheme != "http" or parsed.hostname not in ("localhost", "127.0.0.1"):
            raise ScenarioError("Only local http://localhost or http://127.0.0.1 APIs are allowed")
        if parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/"):
            raise ScenarioError("Pass the API origin only, without paths, credentials or query")
        self.base = base.rstrip("/")

    def call(self, method: str, path: str, payload: object = None):
        body = None if payload is None else json.dumps(payload).encode("utf-8")
        request = Request(
            self.base + path,
            data=body,
            headers={"Content-Type": "application/json"} if body is not None else {},
            method=method,
        )
        try:
            with urlopen(request, timeout=10) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:400]
            raise ScenarioError(f"{method} {path}: HTTP {exc.code} {detail}") from exc
        except (URLError, TimeoutError, ValueError) as exc:
            raise ScenarioError(f"{method} {path}: {exc}") from exc

    def get(self, path: str):
        return self.call("GET", path)

    def post(self, path: str, data: dict):
        return self.call("POST", path, data)

    def patch(self, path: str, data: dict):
        return self.call("PATCH", path, data)

    def delete(self, path: str):
        return self.call("DELETE", path)


def identifier(device: Device) -> str:
    return f"AA:BB:CC:EE:07:{device.suffix:02X}"


def pair(first: str, second: str) -> frozenset[str]:
    return frozenset((first, second))


def inspect_registry(api: Api) -> dict[str, dict]:
    existing = api.get("/api/sensors")
    if not isinstance(existing, list):
        raise ScenarioError("Sensor registry did not return an array")
    known = {identifier(dev): label for label, dev in DEVICES.items()}
    unknown = [sensor for sensor in existing if sensor.get("deviceIdentifier") not in known]
    if unknown:
        raise ScenarioError(
            "Refusing to change a populated workspace with non-demo sensors. "
            "Use a fresh local Docker instance instead."
        )
    found = {}
    for sensor in existing:
        label = known[sensor["deviceIdentifier"]]
        device = DEVICES[label]
        if (label in found or sensor.get("name") != device.name
                or sensor.get("category") != device.category
                or sensor.get("identifierKind") != "MacAddress"
                or sensor.get("telemetryDataType") != "Float"):
            raise ScenarioError(f"Existing demo sensor {label!r} does not match expected fixture")
        found[label] = sensor
    return found


def ensure_sensors(api: Api, existing: dict[str, dict]) -> dict[str, dict]:
    for label, device in DEVICES.items():
        if label not in existing:
            existing[label] = api.post("/api/sensors", {
                "name": device.name,
                "deviceIdentifier": identifier(device),
                "identifierKind": "MacAddress",
                "deploymentLocation": {
                    "facility": "Greenhouse",
                    "zone": "Hydroponics",
                    "nodeId": f"Zone-{device.suffix:02X}",
                },
                "category": device.category,
                "telemetryDataType": "Float",
            })
    return existing


def set_status(api: Api, sensors: dict[str, dict], label: str, status: str) -> None:
    record = sensors[label]
    if record.get("status") != status:
        updated = api.patch(f"/api/sensors/{record['id']}/status", {"status": status})
        sensors[label] = updated


def key(sensors: dict[str, dict], label: str) -> str:
    return "gateway" if label == "gateway" else f"sensor/{sensors[label]['id']}"


def reconcile_links(api: Api, sensors: dict[str, dict], scenario: str) -> None:
    topology = api.get("/api/topology")
    expected = {
        pair(key(sensors, a), key(sensors, b)): cost
        for a, b, cost in EDGES
        if not (scenario == "island" and pair(a, b) == pair("gateway", "backup"))
    }
    existing = {}
    for link in topology["links"]:
        endpoints = pair(link["nodeAKey"], link["nodeBKey"])
        if endpoints not in {
            pair(key(sensors, a), key(sensors, b)) for a, b, _ in EDGES
        }:
            raise ScenarioError(
                "Unexpected topology link detected. Refusing to modify operator-created links."
            )
        if endpoints in existing:
            raise ScenarioError("Duplicate configured link detected")
        existing[endpoints] = link

    for endpoints, link in existing.items():
        if endpoints not in expected:
            api.delete(f"/api/topology/links/{link['id']}")
        elif not math.isclose(link["simulatedEnergyCost"], expected[endpoints]):
            api.patch(f"/api/topology/links/{link['id']}/cost", {
                "simulatedEnergyCost": expected[endpoints]
            })

    for (a, b, cost) in EDGES:
        endpoints = pair(key(sensors, a), key(sensors, b))
        if endpoints in expected and endpoints not in existing:
            api.post("/api/topology/links", {
                "nodeAKey": key(sensors, a),
                "nodeBKey": key(sensors, b),
                "simulatedEnergyCost": cost,
            })


def validate(api: Api, scenario: str, sensors: dict[str, dict]) -> None:
    graph = api.get("/api/topology")
    bfs = api.get("/api/topology/reachability")
    mst = api.get("/api/topology/mst")
    reachable, disconnected, unavailable, total_cost = EXPECTED[scenario]
    checks = {
        "registered sensors": (bfs["registeredSensorCount"], 6),
        "reachable sensors": (bfs["reachableSensorCount"], reachable),
        "disconnected sensors": (bfs["disconnectedSensorCount"], disconnected),
        "unavailable sensors": (bfs["unavailableSensorCount"], unavailable),
        "registered nodes + gateway": (graph["nodeCount"], 7),
        "configured links": (graph["linkCount"], 6 if scenario == "island" else 7),
        "MST selected edges": (mst["selectedEdgeCount"], reachable),
    }
    for name, (observed, expected) in checks.items():
        if observed != expected:
            raise ScenarioError(f"{name}: expected {expected}, received {observed}")
    if not math.isclose(mst["totalSimulatedEnergyCost"], total_cost, abs_tol=1e-9):
        raise ScenarioError(
            f"MST total expected {total_cost}, received {mst['totalSimulatedEnergyCost']}"
        )
    if len({graph["graphVersion"], bfs["graphVersion"], mst["graphVersion"]}) != 1:
        raise ScenarioError("Topology, BFS and MST returned different graph revisions")
    vulnerable = api.get(f"/api/topology/devices/{sensors['vulnerable']['id']}/route")
    expected_vulnerable = "Reachable" if scenario in ("healthy", "recovered") else "Disconnected"
    if vulnerable["status"] != expected_vulnerable:
        raise ScenarioError(
            f"Vulnerable leaf expected {expected_vulnerable}, got {vulnerable['status']}"
        )
    print(
        f"PASS {scenario}: reachable={reachable}, disconnected={disconnected}, "
        f"unavailable={unavailable}, Prim MST total={total_cost:g}; "
        f"graph revision={graph['graphVersion']}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "scenario", choices=tuple(EXPECTED), help="Local topology scenario to apply"
    )
    parser.add_argument(
        "--url", default="http://localhost:8080",
        help="Smart-X loopback origin (default: http://localhost:8080)"
    )
    args = parser.parse_args()
    try:
        api = Api(args.url)
        existing = inspect_registry(api)
        # Check the current graph BEFORE creating any fixture sensors.
        # If it contains unexpected edges, do not partially populate the registry.
        graph = api.get("/api/topology")
        known_ids = {f"sensor/{record['id']}" for record in existing.values()}
        for link in graph["links"]:
            if (link["nodeAKey"] not in known_ids | {"gateway"}
                    or link["nodeBKey"] not in known_ids | {"gateway"}):
                raise ScenarioError("Unexpected existing mesh detected; no changes made")
        sensors = ensure_sensors(api, existing)

        # Create the expected logical links first; then simulate relay failure.
        # The registered sensor state is authoritative; fixture data is not
        # written directly into process-local stores.
        for label in DEVICES:
            if label != "primary":
                set_status(api, sensors, label, "Online")
        set_status(api, sensors, "primary", "Online")
        reconcile_links(api, sensors, args.scenario)
        if args.scenario in ("relay-failure", "island"):
            set_status(api, sensors, "primary", "Offline")
        validate(api, args.scenario, sensors)
        return 0
    except ScenarioError as exc:
        print(f"Scenario failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
