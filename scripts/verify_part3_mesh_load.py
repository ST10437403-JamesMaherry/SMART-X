#!/usr/bin/env python3
"""Verify Smart-X Part 3 mesh scalability through the real HTTP API.

This verification harness deliberately exercises the deployed application surface instead
of importing production implementation classes. It proves that the existing adjacency-list
mesh, BFS reachability, Prim minimum-spanning-tree (MST) projection and selected-device route
repair continue to behave correctly at 256 and 1,000 registered sensor nodes.

The script is intentionally standard-library only so it can run on the same macOS/Python
installation already used by the Smart-X repository without adding a project dependency.

Important evidence semantics
----------------------------
* Setup traffic is excluded from latency summaries. The timings describe repeated read-only
  HTTP queries after a deterministic graph has been constructed.
* Prim's API result is checked against an independent Kruskal implementation in this script.
  Agreement therefore validates the selected total cost without simply trusting one API DTO.
* BFS hop counts are independently recomputed from the configured topology snapshot.
* The API is process-local. Each evidence run MUST start from a fresh API process. This script
  refuses to run if sensors or links already exist, preventing mixed or misleading evidence.
* All links and failures are simulated logical topology state. No physical radio hardware is
  controlled by this script.
"""

from __future__ import annotations

import argparse
import json
import math
import platform
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid
from collections import deque
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence


GATEWAY_KEY = "gateway"
ALLOWED_SENSOR_COUNTS = (256, 1000)
DEFAULT_BASE_URL = "http://127.0.0.1:5095"
DEFAULT_TIMEOUT_SECONDS = 30.0
DEFAULT_TIMING_SAMPLES = 11
FLOAT_TOLERANCE = 1e-9


class VerificationFailure(RuntimeError):
    """Raised when the real API contradicts a required Part 3 invariant."""


@dataclass(frozen=True)
class MeshFixturePlan:
    """Deterministic load shape used for one isolated API process."""

    sensor_count: int
    connected_sensor_count: int
    island_sensor_count: int
    bridge_cost: float = 5.0
    primary_gateway_cost: float = 1.0
    backup_gateway_cost: float = 8.0

    @property
    def expected_initial_mst_cost(self) -> float:
        # Gateway -> first main node plus the connected main chain: one cost-1
        # edge per initially reachable sensor.
        return float(self.connected_sensor_count)

    @property
    def expected_bridged_mst_cost(self) -> float:
        # Once the island is bridged, the final tree contains:
        #   connected_sensor_count main edges
        # + island_sensor_count - 1 island-internal edges
        # + one bridge edge with cost 5.
        return float(
            self.connected_sensor_count
            + max(0, self.island_sensor_count - 1)
            + self.bridge_cost
        )


@dataclass(frozen=True)
class ConfiguredLink:
    """Canonical configured edge returned by POST /api/topology/links."""

    link_id: str
    node_a: str
    node_b: str
    cost: float


class ApiClient:
    """Small JSON HTTP client with explicit status validation and useful failures."""

    def __init__(self, base_url: str, timeout_seconds: float) -> None:
        cleaned = base_url.strip().rstrip("/")
        if not cleaned.startswith(("http://", "https://")):
            raise VerificationFailure("--base-url must begin with http:// or https://")
        if timeout_seconds <= 0 or not math.isfinite(timeout_seconds):
            raise VerificationFailure("--timeout must be a finite value greater than zero")

        self.base_url = cleaned
        self.timeout_seconds = timeout_seconds

    def url(self, path: str) -> str:
        """Join an absolute API path to the configured server without double slashes."""
        if not path.startswith("/"):
            path = "/" + path
        return self.base_url + path

    def request(
        self,
        method: str,
        path: str,
        payload: Mapping[str, Any] | None = None,
        expected_statuses: Sequence[int] = (200,),
    ) -> Any:
        """Send one request and decode a JSON body when present."""
        data: bytes | None = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(
            self.url(path), data=data, headers=headers, method=method.upper()
        )

        try:
            with urllib.request.urlopen(request, timeout=self.timeout_seconds) as response:
                status = response.status
                body = response.read()
                content_type = response.headers.get_content_type()
        except urllib.error.HTTPError as exc:
            status = exc.code
            body = exc.read()
            content_type = exc.headers.get_content_type()
        except urllib.error.URLError as exc:
            raise VerificationFailure(
                f"Cannot reach Smart-X API at {self.base_url}: {exc.reason}"
            ) from exc
        except TimeoutError as exc:
            raise VerificationFailure(
                f"Smart-X API request timed out after {self.timeout_seconds:g}s: "
                f"{method.upper()} {path}"
            ) from exc

        decoded: Any = None
        if body:
            try:
                decoded = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise VerificationFailure(
                    f"{method.upper()} {path} returned a non-JSON response "
                    f"(HTTP {status}, content-type {content_type!r})"
                ) from exc

        if status not in expected_statuses:
            problem = json.dumps(decoded, ensure_ascii=False)[:800]
            raise VerificationFailure(
                f"{method.upper()} {path} returned HTTP {status}; expected "
                f"{list(expected_statuses)}. Body: {problem}"
            )

        return decoded

    def timed_get(self, path: str) -> tuple[Any, float]:
        """Return one successful GET payload and its client-observed elapsed milliseconds."""
        started = time.perf_counter()
        payload = self.request("GET", path, expected_statuses=(200,))
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        return payload, elapsed_ms


def utc_now_iso() -> str:
    """Return an RFC 3339 UTC timestamp suitable for evidence JSON."""
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def build_plan(sensor_count: int) -> MeshFixturePlan:
    """Return the assessment fixture sizes agreed for P3-11."""
    if sensor_count == 256:
        return MeshFixturePlan(256, connected_sensor_count=231, island_sensor_count=25)
    if sensor_count == 1000:
        return MeshFixturePlan(1000, connected_sensor_count=900, island_sensor_count=100)
    raise VerificationFailure(
        f"Unsupported sensor count {sensor_count}; choose one of {ALLOWED_SENSOR_COUNTS}"
    )


def sensor_key(sensor_id: str) -> str:
    """Return the same canonical key shape exposed by GET /api/topology."""
    return f"sensor/{str(uuid.UUID(sensor_id)).lower()}"


def registration_payload(sensor_count: int, index: int, run_id: str) -> dict[str, Any]:
    """Create one deterministic but realistic registered ESP32-style sensor record."""
    if index < 0 or index >= 65536:
        raise ValueError("index must fit into two MAC-address bytes")

    size_hi = (sensor_count >> 8) & 0xFF
    size_lo = sensor_count & 0xFF
    index_hi = (index >> 8) & 0xFF
    index_lo = index & 0xFF
    mac = f"02:50:{size_hi:02X}:{size_lo:02X}:{index_hi:02X}:{index_lo:02X}"

    return {
        "name": f"P3 Mesh {sensor_count:04d} Node {index + 1:04d}",
        "deviceIdentifier": mac,
        "identifierKind": "MacAddress",
        "deploymentLocation": {
            "facility": "P3 Load Verification",
            "zone": f"Mesh-{sensor_count}-{run_id[:6]}",
            "nodeId": f"N-{index + 1:04d}",
        },
        "category": "Environmental",
        "telemetryDataType": "Float",
    }


def status_payload(status: str) -> dict[str, str]:
    """Build the existing PATCH /api/sensors/{id}/status transport request."""
    return {"status": status}


def create_link_payload(node_a: str, node_b: str, cost: float) -> dict[str, Any]:
    """Build the simulation-only topology link request."""
    return {
        "nodeAKey": node_a,
        "nodeBKey": node_b,
        "simulatedEnergyCost": cost,
    }


def _require_dict(payload: Any, context: str) -> dict[str, Any]:
    if not isinstance(payload, dict):
        raise VerificationFailure(f"{context} must be a JSON object")
    return payload


def _require_list(payload: Mapping[str, Any], key: str, context: str) -> list[Any]:
    value = payload.get(key)
    if not isinstance(value, list):
        raise VerificationFailure(f"{context}.{key} must be a JSON array")
    return value


def _require_int(payload: Mapping[str, Any], key: str, context: str) -> int:
    value = payload.get(key)
    if type(value) is not int:
        raise VerificationFailure(f"{context}.{key} must be an integer")
    return value


def _require_number(payload: Mapping[str, Any], key: str, context: str) -> float:
    value = payload.get(key)
    if type(value) not in (int, float) or not math.isfinite(float(value)):
        raise VerificationFailure(f"{context}.{key} must be a finite number")
    return float(value)


def validate_topology_contract(payload: Any) -> dict[str, Any]:
    """Validate the public topology DTO shape before using it as evidence."""
    data = _require_dict(payload, "topology")
    _require_int(data, "graphVersion", "topology")
    if data.get("gatewayKey") != GATEWAY_KEY:
        raise VerificationFailure("topology.gatewayKey must be 'gateway'")
    nodes = _require_list(data, "nodes", "topology")
    links = _require_list(data, "links", "topology")
    if _require_int(data, "nodeCount", "topology") != len(nodes):
        raise VerificationFailure("topology.nodeCount does not match nodes length")
    if _require_int(data, "linkCount", "topology") != len(links):
        raise VerificationFailure("topology.linkCount does not match links length")
    return data


def validate_reachability_contract(payload: Any) -> dict[str, Any]:
    """Validate the BFS projection and its aggregate counts."""
    data = _require_dict(payload, "reachability")
    _require_int(data, "graphVersion", "reachability")
    if data.get("gatewayKey") != GATEWAY_KEY:
        raise VerificationFailure("reachability.gatewayKey must be 'gateway'")
    _require_int(data, "registeredSensorCount", "reachability")
    _require_int(data, "reachableSensorCount", "reachability")
    _require_int(data, "disconnectedSensorCount", "reachability")
    _require_int(data, "unavailableSensorCount", "reachability")
    _require_list(data, "reachableNodes", "reachability")
    _require_list(data, "unavailableSensorKeys", "reachability")
    _require_list(data, "disconnectedComponents", "reachability")
    return data


def validate_mst_contract(payload: Any) -> dict[str, Any]:
    """Validate the gateway-rooted Prim projection and selected-edge counts."""
    data = _require_dict(payload, "mst")
    _require_int(data, "graphVersion", "mst")
    if data.get("gatewayKey") != GATEWAY_KEY:
        raise VerificationFailure("mst.gatewayKey must be 'gateway'")
    _require_number(data, "totalSimulatedEnergyCost", "mst")
    _require_int(data, "registeredSensorCount", "mst")
    _require_int(data, "reachableSensorCount", "mst")
    _require_int(data, "disconnectedSensorCount", "mst")
    _require_int(data, "unavailableSensorCount", "mst")
    selected = _require_list(data, "selectedEdges", "mst")
    if _require_int(data, "selectedEdgeCount", "mst") != len(selected):
        raise VerificationFailure("mst.selectedEdgeCount does not match selectedEdges length")
    _require_list(data, "reachedNodeKeys", "mst")
    _require_list(data, "unavailableSensorKeys", "mst")
    _require_list(data, "disconnectedComponents", "mst")
    return data


def validate_route_contract(payload: Any) -> dict[str, Any]:
    """Validate one same-revision BFS + Prim selected-device route DTO."""
    data = _require_dict(payload, "route")
    _require_int(data, "graphVersion", "route")
    if not isinstance(data.get("sensorProfileId"), str):
        raise VerificationFailure("route.sensorProfileId must be a GUID string")
    if data.get("status") not in {"Reachable", "Disconnected", "Unavailable"}:
        raise VerificationFailure("route.status is not a supported mesh route status")
    for key in ("bfs", "mst"):
        route = _require_dict(data.get(key), f"route.{key}")
        if route.get("status") not in {"Reachable", "Disconnected", "Unavailable"}:
            raise VerificationFailure(f"route.{key}.status is invalid")
        _require_list(route, "pathToGateway", f"route.{key}")
    return data


def topology_edges(topology: Mapping[str, Any]) -> list[tuple[str, str, float, str]]:
    """Project API link rows into a compact algorithm-friendly representation."""
    edges: list[tuple[str, str, float, str]] = []
    for raw in _require_list(topology, "links", "topology"):
        link = _require_dict(raw, "topology.links[]")
        link_id = link.get("id")
        node_a = link.get("nodeAKey")
        node_b = link.get("nodeBKey")
        cost = link.get("simulatedEnergyCost")
        if not all(isinstance(item, str) and item for item in (link_id, node_a, node_b)):
            raise VerificationFailure("Each topology link must expose id/nodeAKey/nodeBKey")
        if type(cost) not in (int, float) or not math.isfinite(float(cost)) or cost <= 0:
            raise VerificationFailure("Each topology link must expose a positive finite cost")
        edges.append((node_a, node_b, float(cost), link_id))
    return edges


def eligible_node_keys(topology: Mapping[str, Any]) -> set[str]:
    """Return the gateway plus forwarding-eligible sensor nodes from one snapshot."""
    eligible = {GATEWAY_KEY}
    for raw in _require_list(topology, "nodes", "topology"):
        node = _require_dict(raw, "topology.nodes[]")
        key = node.get("key")
        if key == GATEWAY_KEY:
            continue
        if isinstance(key, str) and node.get("isForwardingEligible") is True:
            eligible.add(key)
    return eligible


def build_adjacency(
    nodes: Iterable[str], edges: Iterable[tuple[str, str, float, str]]
) -> dict[str, list[tuple[str, float, str]]]:
    """Build an undirected adjacency list from the authoritative API snapshot."""
    adjacency = {key: [] for key in nodes}
    for node_a, node_b, cost, link_id in edges:
        if node_a not in adjacency or node_b not in adjacency:
            continue
        adjacency[node_a].append((node_b, cost, link_id))
        adjacency[node_b].append((node_a, cost, link_id))
    return adjacency


def bfs_hops(
    adjacency: Mapping[str, Sequence[tuple[str, float, str]]], start: str = GATEWAY_KEY
) -> dict[str, int]:
    """Independently calculate fewest-hop distances from the gateway."""
    if start not in adjacency:
        return {}
    hops = {start: 0}
    queue: deque[str] = deque([start])
    while queue:
        current = queue.popleft()
        for neighbour, _cost, _link_id in adjacency[current]:
            if neighbour in hops:
                continue
            hops[neighbour] = hops[current] + 1
            queue.append(neighbour)
    return hops


def bfs_sensor_to_gateway_path(
    adjacency: Mapping[str, Sequence[tuple[str, float, str]]], sensor: str
) -> list[str]:
    """Return one minimum-hop sensor-to-gateway path using local BFS evidence."""
    if sensor not in adjacency or GATEWAY_KEY not in adjacency:
        return []
    previous: dict[str, str | None] = {GATEWAY_KEY: None}
    queue: deque[str] = deque([GATEWAY_KEY])
    while queue:
        current = queue.popleft()
        if current == sensor:
            break
        for neighbour, _cost, _link_id in adjacency[current]:
            if neighbour in previous:
                continue
            previous[neighbour] = current
            queue.append(neighbour)
    if sensor not in previous:
        return []

    path = [sensor]
    current = sensor
    while current != GATEWAY_KEY:
        parent = previous[current]
        if parent is None:
            raise AssertionError("BFS predecessor chain terminated before gateway")
        path.append(parent)
        current = parent
    return path


class _DisjointSet:
    """Path-compressed union-find used only by the independent Kruskal proof."""

    def __init__(self, keys: Iterable[str]) -> None:
        self.parent = {key: key for key in keys}
        self.rank = {key: 0 for key in self.parent}

    def find(self, key: str) -> str:
        parent = self.parent[key]
        if parent != key:
            self.parent[key] = self.find(parent)
        return self.parent[key]

    def union(self, left: str, right: str) -> bool:
        root_left = self.find(left)
        root_right = self.find(right)
        if root_left == root_right:
            return False
        if self.rank[root_left] < self.rank[root_right]:
            root_left, root_right = root_right, root_left
        self.parent[root_right] = root_left
        if self.rank[root_left] == self.rank[root_right]:
            self.rank[root_left] += 1
        return True


def kruskal_gateway_tree(
    topology: Mapping[str, Any],
) -> tuple[float, list[str], list[str]]:
    """Compute an independent MST for ONLY the gateway-reachable eligible component.

    The production API uses Prim. Using Kruskal here gives independent algorithmic evidence.
    Disconnected eligible islands are intentionally excluded because the API's MST is rooted at
    the gateway rather than returning a minimum spanning forest for every disconnected component.
    """
    eligible = eligible_node_keys(topology)
    edges = topology_edges(topology)
    adjacency = build_adjacency(eligible, edges)
    reachable = set(bfs_hops(adjacency))
    dsu = _DisjointSet(reachable)
    selected_ids: list[str] = []
    total = 0.0

    candidates = [edge for edge in edges if edge[0] in reachable and edge[1] in reachable]
    candidates.sort(key=lambda edge: (edge[2], edge[3]))
    for node_a, node_b, cost, link_id in candidates:
        if dsu.union(node_a, node_b):
            total += cost
            selected_ids.append(link_id)
    return total, sorted(reachable), selected_ids


def percentile_nearest_rank(values: Sequence[float], percentile: float) -> float:
    """Return a deterministic nearest-rank percentile for small HTTP sample sets."""
    if not values:
        raise ValueError("values cannot be empty")
    if not 0 < percentile <= 100:
        raise ValueError("percentile must be in (0, 100]")
    ordered = sorted(float(value) for value in values)
    rank = max(1, math.ceil((percentile / 100.0) * len(ordered)))
    return ordered[rank - 1]


def timing_summary(values: Sequence[float]) -> dict[str, Any]:
    """Summarise client-observed read latency without imposing pass/fail timing thresholds."""
    if not values:
        raise VerificationFailure("At least one timing sample is required")
    return {
        "samples": len(values),
        "minMs": round(min(values), 3),
        "medianMs": round(statistics.median(values), 3),
        "p95Ms": round(percentile_nearest_rank(values, 95), 3),
        "maxMs": round(max(values), 3),
    }


def assert_close(actual: float, expected: float, context: str) -> None:
    if not math.isclose(actual, expected, rel_tol=0.0, abs_tol=FLOAT_TOLERANCE):
        raise VerificationFailure(f"{context}: expected {expected:g}, observed {actual:g}")


def assert_same_graph_version(*payloads: Mapping[str, Any]) -> None:
    versions = {int(payload["graphVersion"]) for payload in payloads}
    if len(versions) != 1:
        raise VerificationFailure(
            f"Expected same-revision topology projections, observed graph versions {sorted(versions)}"
        )


def assert_api_bfs_matches_snapshot(
    topology: Mapping[str, Any], reachability: Mapping[str, Any]
) -> None:
    """Compare every API hop count with an independently calculated BFS result."""
    eligible = eligible_node_keys(topology)
    adjacency = build_adjacency(eligible, topology_edges(topology))
    expected = bfs_hops(adjacency)

    observed: dict[str, int] = {}
    for raw in _require_list(reachability, "reachableNodes", "reachability"):
        item = _require_dict(raw, "reachability.reachableNodes[]")
        key = item.get("nodeKey")
        hop = item.get("hopCount")
        if not isinstance(key, str) or type(hop) is not int:
            raise VerificationFailure("reachableNodes entries require nodeKey and integer hopCount")
        observed[key] = hop

    if observed != expected:
        missing = sorted(set(expected) - set(observed))[:8]
        extra = sorted(set(observed) - set(expected))[:8]
        mismatched = sorted(
            key for key in set(expected) & set(observed) if expected[key] != observed[key]
        )[:8]
        raise VerificationFailure(
            "API BFS differs from independent BFS: "
            f"missing={missing}, extra={extra}, mismatched={mismatched}"
        )


def assert_api_mst_matches_kruskal(
    topology: Mapping[str, Any], mst: Mapping[str, Any]
) -> dict[str, Any]:
    """Cross-check Prim's total and tree size using independent Kruskal."""
    kruskal_cost, reachable_keys, selected_ids = kruskal_gateway_tree(topology)
    api_cost = _require_number(mst, "totalSimulatedEnergyCost", "mst")
    assert_close(api_cost, kruskal_cost, "Prim/Kruskal total cost")

    expected_edge_count = max(0, len(reachable_keys) - 1)
    if mst["selectedEdgeCount"] != expected_edge_count:
        raise VerificationFailure(
            "Prim selectedEdgeCount differs from reachable-tree cardinality: "
            f"expected {expected_edge_count}, observed {mst['selectedEdgeCount']}"
        )
    if len(selected_ids) != expected_edge_count:
        raise VerificationFailure("Independent Kruskal did not build a complete reachable tree")

    return {
        "totalSimulatedEnergyCost": round(kruskal_cost, 9),
        "selectedEdgeCount": len(selected_ids),
        "reachableNodeCountIncludingGateway": len(reachable_keys),
    }


def assert_route_matches_snapshot(
    topology: Mapping[str, Any], route: Mapping[str, Any], target_key: str
) -> None:
    """Check the API BFS path against the same authoritative topology snapshot."""
    eligible = eligible_node_keys(topology)
    adjacency = build_adjacency(eligible, topology_edges(topology))
    expected_path = bfs_sensor_to_gateway_path(adjacency, target_key)
    bfs = _require_dict(route.get("bfs"), "route.bfs")

    if expected_path:
        if route.get("status") != "Reachable" or bfs.get("status") != "Reachable":
            raise VerificationFailure("Connected target did not return Reachable route status")
        observed_path = bfs.get("pathToGateway")
        if observed_path != expected_path:
            raise VerificationFailure(
                f"Route BFS path differs from independent BFS: expected {expected_path}, "
                f"observed {observed_path}"
            )
        if bfs.get("hopCount") != len(expected_path) - 1:
            raise VerificationFailure("Route BFS hopCount disagrees with returned path length")
    else:
        if route.get("status") != "Disconnected":
            raise VerificationFailure("Gateway-disconnected eligible target must be Disconnected")
        if bfs.get("pathToGateway") != [] or bfs.get("hopCount") is not None:
            raise VerificationFailure("Disconnected route must not fabricate a BFS path")


def _progress(label: str, completed: int, total: int) -> None:
    if completed == total or completed == 1 or completed % 100 == 0:
        print(f"  {label}: {completed}/{total}", flush=True)


def register_online_sensors(
    client: ApiClient, plan: MeshFixturePlan, run_id: str
) -> list[str]:
    """Register every sensor through POST /api/sensors, then mark it Online."""
    sensor_ids: list[str] = []
    print(f"Registering {plan.sensor_count} sensors through the real API...")
    for index in range(plan.sensor_count):
        created = _require_dict(
            client.request(
                "POST",
                "/api/sensors",
                registration_payload(plan.sensor_count, index, run_id),
                expected_statuses=(201,),
            ),
            "sensor registration",
        )
        sensor_id = created.get("id")
        if not isinstance(sensor_id, str):
            raise VerificationFailure("Sensor registration did not return an id")
        # Validate canonical GUID syntax before using the ID in topology node keys.
        uuid.UUID(sensor_id)
        client.request(
            "PATCH",
            f"/api/sensors/{sensor_id}/status",
            status_payload("Online"),
            expected_statuses=(200,),
        )
        sensor_ids.append(sensor_id)
        _progress("registered+online", index + 1, plan.sensor_count)
    return sensor_ids


def create_link(client: ApiClient, node_a: str, node_b: str, cost: float) -> ConfiguredLink:
    payload = _require_dict(
        client.request(
            "POST",
            "/api/topology/links",
            create_link_payload(node_a, node_b, cost),
            expected_statuses=(201,),
        ),
        "link creation",
    )
    link_id = payload.get("id")
    if not isinstance(link_id, str):
        raise VerificationFailure("Link creation did not return an id")
    uuid.UUID(link_id)
    return ConfiguredLink(
        link_id=link_id,
        node_a=str(payload.get("nodeAKey")),
        node_b=str(payload.get("nodeBKey")),
        cost=float(payload.get("simulatedEnergyCost")),
    )


def construct_initial_mesh(
    client: ApiClient, sensor_ids: Sequence[str], plan: MeshFixturePlan
) -> dict[str, Any]:
    """Create a connected backbone, disconnected island, backup route and harmless cycles."""
    keys = [sensor_key(sensor_id) for sensor_id in sensor_ids]
    main = keys[: plan.connected_sensor_count]
    island = keys[plan.connected_sensor_count :]
    if len(main) < 21 or len(island) < 1:
        raise VerificationFailure("Fixture requires a sufficiently large main component and island")

    expected_mutations = plan.connected_sensor_count + (plan.island_sensor_count - 1) + 3
    completed = 0

    print("Creating deterministic weighted mesh links...")
    primary = create_link(client, GATEWAY_KEY, main[0], plan.primary_gateway_cost)
    completed += 1
    _progress("configured links", completed, expected_mutations)

    for index in range(1, len(main)):
        create_link(client, main[index - 1], main[index], 1.0)
        completed += 1
        _progress("configured links", completed, expected_mutations)

    for index in range(1, len(island)):
        create_link(client, island[index - 1], island[index], 1.0)
        completed += 1
        _progress("configured links", completed, expected_mutations)

    # Expensive backup keeps the main component alive if the primary gateway
    # edge is removed, but cannot displace cost-1 edges from the initial MST.
    backup = create_link(client, GATEWAY_KEY, main[1], plan.backup_gateway_cost)
    completed += 1
    _progress("configured links", completed, expected_mutations)

    # Two redundant cycle edges prove the traversal/MST code handles a graph,
    # not just a tree. Their weights exceed the equivalent chain cost, so the
    # agreed deterministic MST totals remain 231/900 before the island bridge.
    create_link(client, main[4], main[10], 20.0)
    completed += 1
    _progress("configured links", completed, expected_mutations)
    create_link(client, main[10], main[20], 25.0)
    completed += 1
    _progress("configured links", completed, expected_mutations)

    if completed != expected_mutations:
        raise AssertionError("Unexpected fixture link count")

    return {
        "mainKeys": main,
        "islandKeys": island,
        "primaryGatewayLink": asdict(primary),
        "backupGatewayLink": asdict(backup),
    }


def capture_projection(client: ApiClient, target_sensor_id: str) -> dict[str, Any]:
    """Read topology/BFS/MST/route and require one shared graph version."""
    topology = validate_topology_contract(client.request("GET", "/api/topology"))
    reachability = validate_reachability_contract(
        client.request("GET", "/api/topology/reachability")
    )
    mst = validate_mst_contract(client.request("GET", "/api/topology/mst"))
    route = validate_route_contract(
        client.request("GET", f"/api/topology/devices/{target_sensor_id}/route")
    )
    assert_same_graph_version(topology, reachability, mst, route)
    assert_api_bfs_matches_snapshot(topology, reachability)
    kruskal = assert_api_mst_matches_kruskal(topology, mst)
    assert_route_matches_snapshot(topology, route, sensor_key(target_sensor_id))
    return {
        "topology": topology,
        "reachability": reachability,
        "mst": mst,
        "route": route,
        "independentKruskal": kruskal,
    }


def compact_projection(projection: Mapping[str, Any]) -> dict[str, Any]:
    """Keep evidence useful without duplicating thousands of topology rows in JSON."""
    topology = projection["topology"]
    reachability = projection["reachability"]
    mst = projection["mst"]
    route = projection["route"]
    return {
        "graphVersion": topology["graphVersion"],
        "nodeCount": topology["nodeCount"],
        "linkCount": topology["linkCount"],
        "registeredSensorCount": reachability["registeredSensorCount"],
        "reachableSensorCount": reachability["reachableSensorCount"],
        "disconnectedSensorCount": reachability["disconnectedSensorCount"],
        "unavailableSensorCount": reachability["unavailableSensorCount"],
        "disconnectedComponentSizes": [
            len(component.get("sensorKeys", []))
            for component in reachability["disconnectedComponents"]
            if isinstance(component, dict)
        ],
        "mstTotalSimulatedEnergyCost": mst["totalSimulatedEnergyCost"],
        "mstSelectedEdgeCount": mst["selectedEdgeCount"],
        "independentKruskal": projection["independentKruskal"],
        "selectedDeviceRoute": route,
    }


def assert_projection_counts(
    projection: Mapping[str, Any],
    expected_registered: int,
    expected_reachable: int,
    expected_disconnected: int,
    expected_mst_cost: float,
) -> None:
    reachability = projection["reachability"]
    mst = projection["mst"]
    topology = projection["topology"]

    expected = {
        "registeredSensorCount": expected_registered,
        "reachableSensorCount": expected_reachable,
        "disconnectedSensorCount": expected_disconnected,
        "unavailableSensorCount": 0,
    }
    for key, value in expected.items():
        if reachability.get(key) != value:
            raise VerificationFailure(
                f"reachability.{key}: expected {value}, observed {reachability.get(key)}"
            )
        if mst.get(key) != value:
            raise VerificationFailure(f"mst.{key}: expected {value}, observed {mst.get(key)}")

    if topology["nodeCount"] != expected_registered + 1:
        raise VerificationFailure(
            f"topology.nodeCount: expected {expected_registered + 1}, "
            f"observed {topology['nodeCount']}"
        )
    assert_close(
        float(mst["totalSimulatedEnergyCost"]), expected_mst_cost, "MST total cost"
    )
    if mst["selectedEdgeCount"] != expected_reachable:
        raise VerificationFailure(
            f"MST selectedEdgeCount: expected {expected_reachable}, "
            f"observed {mst['selectedEdgeCount']}"
        )


def verify_invalid_requests(
    client: ApiClient, existing_a: str, existing_b: str
) -> dict[str, Any]:
    """Prove high-load state still rejects malformed/duplicate operations atomically."""
    before = validate_topology_contract(client.request("GET", "/api/topology"))
    before_version = before["graphVersion"]
    before_links = before["linkCount"]

    duplicate_problem = client.request(
        "POST",
        "/api/topology/links",
        create_link_payload(existing_a, existing_b, 99.0),
        expected_statuses=(400,),
    )
    invalid_cost_problem = client.request(
        "POST",
        "/api/topology/links",
        create_link_payload(existing_a, existing_b, 0.0),
        expected_statuses=(400,),
    )
    empty_route_problem = client.request(
        "GET",
        "/api/topology/devices/00000000-0000-0000-0000-000000000000/route",
        expected_statuses=(400,),
    )
    unknown_route_problem = client.request(
        "GET",
        f"/api/topology/devices/{uuid.uuid4()}/route",
        expected_statuses=(404,),
    )

    after = validate_topology_contract(client.request("GET", "/api/topology"))
    if after["graphVersion"] != before_version or after["linkCount"] != before_links:
        raise VerificationFailure("Rejected topology requests mutated the authoritative graph")

    return {
        "duplicateLinkStatus": int(duplicate_problem.get("status", 400))
        if isinstance(duplicate_problem, dict)
        else 400,
        "invalidCostStatus": int(invalid_cost_problem.get("status", 400))
        if isinstance(invalid_cost_problem, dict)
        else 400,
        "emptySensorRouteStatus": int(empty_route_problem.get("status", 400))
        if isinstance(empty_route_problem, dict)
        else 400,
        "unknownSensorRouteStatus": int(unknown_route_problem.get("status", 404))
        if isinstance(unknown_route_problem, dict)
        else 404,
        "graphVersionUnchanged": after["graphVersion"] == before_version,
        "linkCountUnchanged": after["linkCount"] == before_links,
    }


def collect_read_timings(
    client: ApiClient, sensor_id: str, samples: int
) -> dict[str, Any]:
    """Measure stable read-only query timings after correctness verification."""
    if samples < 3:
        raise VerificationFailure("--samples must be at least 3")

    paths = {
        "topology": "/api/topology",
        "reachabilityBfs": "/api/topology/reachability",
        "minimumSpanningTree": "/api/topology/mst",
        "selectedDeviceRoute": f"/api/topology/devices/{sensor_id}/route",
    }
    summaries: dict[str, Any] = {}
    for label, path in paths.items():
        # One unmeasured warm-up reduces first-request noise without hiding the
        # actual payload/algorithm cost of the measured samples.
        client.request("GET", path, expected_statuses=(200,))
        timings = [client.timed_get(path)[1] for _ in range(samples)]
        summaries[label] = timing_summary(timings)
        print(
            f"  {label}: p50={summaries[label]['medianMs']:.3f} ms, "
            f"p95={summaries[label]['p95Ms']:.3f} ms"
        )
    return summaries


def verify_clean_start(client: ApiClient) -> None:
    """Refuse evidence collection against a dirty process-local mesh."""
    sensors = client.request("GET", "/api/sensors", expected_statuses=(200,))
    if not isinstance(sensors, list):
        raise VerificationFailure("GET /api/sensors did not return an array")
    topology = validate_topology_contract(client.request("GET", "/api/topology"))
    if sensors or topology["nodeCount"] != 1 or topology["linkCount"] != 0:
        raise VerificationFailure(
            "P3 mesh verification requires a fresh API process: expected zero registered "
            "sensors, one logical gateway node and zero configured links. Restart the API."
        )


def run_verification(
    client: ApiClient,
    plan: MeshFixturePlan,
    samples: int,
) -> dict[str, Any]:
    """Execute the complete deterministic P3-11 large-mesh proof."""
    started = utc_now_iso()
    run_id = uuid.uuid4().hex[:12]
    verify_clean_start(client)

    sensor_ids = register_online_sensors(client, plan, run_id)
    fixture = construct_initial_mesh(client, sensor_ids, plan)
    main_keys: list[str] = fixture["mainKeys"]
    island_keys: list[str] = fixture["islandKeys"]
    target_sensor_id = sensor_ids[0]

    print("Checking initial disconnected-island BFS and Prim/Kruskal agreement...")
    initial = capture_projection(client, target_sensor_id)
    assert_projection_counts(
        initial,
        expected_registered=plan.sensor_count,
        expected_reachable=plan.connected_sensor_count,
        expected_disconnected=plan.island_sensor_count,
        expected_mst_cost=plan.expected_initial_mst_cost,
    )
    component_sizes = sorted(
        len(component.get("sensorKeys", []))
        for component in initial["reachability"]["disconnectedComponents"]
        if isinstance(component, dict)
    )
    if component_sizes != [plan.island_sensor_count]:
        raise VerificationFailure(
            "Initial disconnected component does not match the planned island"
        )

    print("Bridging the isolated component and rechecking the full spanning tree...")
    bridge = create_link(client, main_keys[-1], island_keys[0], plan.bridge_cost)
    bridged = capture_projection(client, target_sensor_id)
    assert_projection_counts(
        bridged,
        expected_registered=plan.sensor_count,
        expected_reachable=plan.sensor_count,
        expected_disconnected=0,
        expected_mst_cost=plan.expected_bridged_mst_cost,
    )

    primary_link = fixture["primaryGatewayLink"]
    backup_link = fixture["backupGatewayLink"]

    print("Removing primary gateway edge and proving backup-route repair...")
    client.request(
        "DELETE",
        f"/api/topology/links/{primary_link['link_id']}",
        expected_statuses=(204,),
    )
    rerouted = capture_projection(client, target_sensor_id)
    assert_projection_counts(
        rerouted,
        expected_registered=plan.sensor_count,
        expected_reachable=plan.sensor_count,
        expected_disconnected=0,
        expected_mst_cost=(
            plan.expected_bridged_mst_cost
            - plan.primary_gateway_cost
            + plan.backup_gateway_cost
        ),
    )
    if rerouted["route"]["bfs"]["hopCount"] != 2:
        raise VerificationFailure("Backup reroute should place the first sensor two BFS hops from gateway")

    print("Removing backup gateway edge and proving complete disconnection...")
    client.request(
        "DELETE",
        f"/api/topology/links/{backup_link['link_id']}",
        expected_statuses=(204,),
    )
    disconnected = capture_projection(client, target_sensor_id)
    assert_projection_counts(
        disconnected,
        expected_registered=plan.sensor_count,
        expected_reachable=0,
        expected_disconnected=plan.sensor_count,
        expected_mst_cost=0.0,
    )
    if disconnected["route"]["status"] != "Disconnected":
        raise VerificationFailure("Target route must report Disconnected with both gateway edges removed")

    print("Restoring the primary gateway edge and proving self-healing reachability...")
    restored_primary = create_link(
        client, GATEWAY_KEY, main_keys[0], plan.primary_gateway_cost
    )
    restored = capture_projection(client, target_sensor_id)
    assert_projection_counts(
        restored,
        expected_registered=plan.sensor_count,
        expected_reachable=plan.sensor_count,
        expected_disconnected=0,
        expected_mst_cost=plan.expected_bridged_mst_cost,
    )
    if restored["route"]["bfs"]["hopCount"] != 1:
        raise VerificationFailure("Restored primary gateway link must return a one-hop BFS route")

    print("Checking invalid-request safety against the populated graph...")
    invalid_requests = verify_invalid_requests(client, GATEWAY_KEY, main_keys[0])

    print(f"Collecting {samples} read-only timing samples per topology projection...")
    timings = collect_read_timings(client, target_sensor_id, samples)

    finished = utc_now_iso()
    return {
        "result": "PASS",
        "runId": run_id,
        "startedAtUtc": started,
        "completedAtUtc": finished,
        "baseUrl": client.base_url,
        "fixture": {
            "requestedSensorCount": plan.sensor_count,
            "connectedSensorCountBeforeBridge": plan.connected_sensor_count,
            "disconnectedIslandSensorCountBeforeBridge": plan.island_sensor_count,
            "bridgeCost": plan.bridge_cost,
            "primaryGatewayCost": plan.primary_gateway_cost,
            "backupGatewayCost": plan.backup_gateway_cost,
            "expectedInitialMstCost": plan.expected_initial_mst_cost,
            "expectedBridgedMstCost": plan.expected_bridged_mst_cost,
            "primaryGatewayLinkIdRemoved": primary_link["link_id"],
            "backupGatewayLinkIdRemoved": backup_link["link_id"],
            "bridgeLinkId": bridge.link_id,
            "restoredPrimaryGatewayLinkId": restored_primary.link_id,
        },
        "initialDisconnectedIsland": compact_projection(initial),
        "afterIslandBridge": compact_projection(bridged),
        "afterPrimaryGatewayRemoval": compact_projection(rerouted),
        "afterAllGatewayLinksRemoved": compact_projection(disconnected),
        "afterPrimaryGatewayRestoration": compact_projection(restored),
        "invalidRequestChecks": invalid_requests,
        "readTimings": timings,
        "environment": {
            "platform": platform.platform(),
            "pythonVersion": platform.python_version(),
        },
        "notes": [
            "All sensors, statuses and links were created through the real Smart-X HTTP API.",
            "BFS hop counts were independently recomputed from the authoritative topology snapshot.",
            "The production Prim MST total was independently cross-checked with Kruskal's algorithm.",
            "The disconnected island was genuine configured graph state; no route was fabricated.",
            "Gateway-link removal exercised backup rerouting, complete disconnection and restoration.",
            "Read timings exclude setup and are observed p50/p95 evidence, not CI latency thresholds.",
            "The simulated mesh is process-local; each evidence size requires a freshly restarted API.",
        ],
    }


def write_evidence(path: Path, evidence: Mapping[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify Smart-X P3 large-mesh BFS, MST and rerouting through the real API."
    )
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--sensors", type=int, choices=ALLOWED_SENSOR_COUNTS, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--samples", type=int, default=DEFAULT_TIMING_SAMPLES)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        plan = build_plan(args.sensors)
        client = ApiClient(args.base_url, args.timeout)
        evidence = run_verification(client, plan, args.samples)
        write_evidence(args.output, evidence)
        print("\nPASS  Smart-X Part 3 large-mesh verification completed")
        print(f"      sensors: {args.sensors}")
        print(
            "      initial: "
            f"reachable={plan.connected_sensor_count}, "
            f"disconnected={plan.island_sensor_count}, "
            f"MST={plan.expected_initial_mst_cost:g}"
        )
        print(
            "      bridged: "
            f"reachable={plan.sensor_count}, disconnected=0, "
            f"MST={plan.expected_bridged_mst_cost:g}"
        )
        print(f"      evidence: {args.output}")
        return 0
    except (VerificationFailure, ValueError) as exc:
        print(f"\nFAIL  {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
