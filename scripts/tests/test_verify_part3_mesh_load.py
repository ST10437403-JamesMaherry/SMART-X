#!/usr/bin/env python3
"""Unit tests for the P3-11 large-mesh verification harness.

These tests exercise the verifier's independent algorithms and evidence guards. They do not
replace the real-API run; the committed mesh-256.json and mesh-1000.json must still be generated
against fresh Smart-X API processes before Commit 2 is created.
"""

from __future__ import annotations

import importlib.util
import math
import sys
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify_part3_mesh_load.py"
SPEC = importlib.util.spec_from_file_location("verify_part3_mesh_load", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT}")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class MeshLoadVerifierTests(unittest.TestCase):
    def _topology(self, nodes, links, version=7):
        return {
            "graphVersion": version,
            "gatewayKey": "gateway",
            "nodeCount": len(nodes),
            "linkCount": len(links),
            "nodes": nodes,
            "links": links,
        }

    @staticmethod
    def _node(key, eligible=True):
        return {
            "key": key,
            "kind": "Gateway" if key == "gateway" else "Sensor",
            "sensorProfileId": None if key == "gateway" else "00000000-0000-0000-0000-000000000001",
            "name": None,
            "deviceIdentifier": None,
            "identifierKind": None,
            "category": None,
            "status": None if key == "gateway" else "Online",
            "isForwardingEligible": True if key == "gateway" else eligible,
            "telemetryDataType": None,
            "deploymentLocation": None,
        }

    @staticmethod
    def _link(link_id, a, b, cost):
        return {
            "id": link_id,
            "nodeAKey": a,
            "nodeBKey": b,
            "simulatedEnergyCost": cost,
        }

    # 01
    def test_plan_256_matches_agreed_fixture(self):
        plan = MODULE.build_plan(256)
        self.assertEqual(231, plan.connected_sensor_count)
        self.assertEqual(25, plan.island_sensor_count)
        self.assertEqual(231.0, plan.expected_initial_mst_cost)
        self.assertEqual(260.0, plan.expected_bridged_mst_cost)

    # 02
    def test_plan_1000_matches_agreed_fixture(self):
        plan = MODULE.build_plan(1000)
        self.assertEqual(900, plan.connected_sensor_count)
        self.assertEqual(100, plan.island_sensor_count)
        self.assertEqual(900.0, plan.expected_initial_mst_cost)
        self.assertEqual(1004.0, plan.expected_bridged_mst_cost)

    # 03
    def test_unsupported_fixture_size_is_rejected(self):
        with self.assertRaises(MODULE.VerificationFailure):
            MODULE.build_plan(512)

    # 04
    def test_registration_payload_is_valid_and_deterministic(self):
        payload = MODULE.registration_payload(256, 1, "abcdef123456")
        self.assertEqual("MacAddress", payload["identifierKind"])
        self.assertEqual("02:50:01:00:00:01", payload["deviceIdentifier"])
        self.assertEqual("Float", payload["telemetryDataType"])
        self.assertIn("Mesh-256-abcdef", payload["deploymentLocation"]["zone"])

    # 05
    def test_status_payload_uses_existing_contract(self):
        self.assertEqual({"status": "Online"}, MODULE.status_payload("Online"))

    # 06
    def test_link_payload_uses_existing_contract(self):
        payload = MODULE.create_link_payload("gateway", "sensor/a", 5.0)
        self.assertEqual("gateway", payload["nodeAKey"])
        self.assertEqual("sensor/a", payload["nodeBKey"])
        self.assertEqual(5.0, payload["simulatedEnergyCost"])

    # 07
    def test_api_url_joining_has_one_separator(self):
        client = MODULE.ApiClient("http://127.0.0.1:5095/", 1.0)
        self.assertEqual("http://127.0.0.1:5095/api/topology", client.url("api/topology"))
        self.assertEqual("http://127.0.0.1:5095/api/topology", client.url("/api/topology"))

    # 08
    def test_bfs_calculates_minimum_hops_in_cycle(self):
        edges = [
            ("gateway", "a", 1.0, "1"),
            ("a", "b", 1.0, "2"),
            ("gateway", "b", 9.0, "3"),
        ]
        adjacency = MODULE.build_adjacency({"gateway", "a", "b"}, edges)
        self.assertEqual({"gateway": 0, "a": 1, "b": 1}, MODULE.bfs_hops(adjacency))

    # 09
    def test_bfs_omits_disconnected_island(self):
        edges = [("gateway", "a", 1.0, "1"), ("x", "y", 1.0, "2")]
        adjacency = MODULE.build_adjacency({"gateway", "a", "x", "y"}, edges)
        self.assertEqual({"gateway": 0, "a": 1}, MODULE.bfs_hops(adjacency))

    # 10
    def test_bfs_returns_sensor_to_gateway_path(self):
        edges = [("gateway", "a", 1.0, "1"), ("a", "b", 1.0, "2")]
        adjacency = MODULE.build_adjacency({"gateway", "a", "b"}, edges)
        self.assertEqual(["b", "a", "gateway"], MODULE.bfs_sensor_to_gateway_path(adjacency, "b"))

    # 11
    def test_kruskal_excludes_disconnected_component(self):
        nodes = [self._node("gateway"), self._node("a"), self._node("b"), self._node("x")]
        links = [
            self._link("00000000-0000-0000-0000-000000000001", "gateway", "a", 1.0),
            self._link("00000000-0000-0000-0000-000000000002", "a", "b", 1.0),
        ]
        topology = self._topology(nodes, links)
        cost, reached, selected = MODULE.kruskal_gateway_tree(topology)
        self.assertEqual(2.0, cost)
        self.assertEqual(["a", "b", "gateway"], reached)
        self.assertEqual(2, len(selected))

    # 12
    def test_kruskal_ignores_expensive_backup_when_cheaper_tree_exists(self):
        nodes = [self._node("gateway"), self._node("a"), self._node("b")]
        links = [
            self._link("00000000-0000-0000-0000-000000000001", "gateway", "a", 1.0),
            self._link("00000000-0000-0000-0000-000000000002", "a", "b", 1.0),
            self._link("00000000-0000-0000-0000-000000000003", "gateway", "b", 8.0),
        ]
        cost, _reached, selected = MODULE.kruskal_gateway_tree(self._topology(nodes, links))
        self.assertEqual(2.0, cost)
        self.assertEqual(2, len(selected))

    # 13
    def test_unavailable_sensor_is_excluded_from_algorithms(self):
        nodes = [self._node("gateway"), self._node("a", eligible=False), self._node("b")]
        links = [
            self._link("00000000-0000-0000-0000-000000000001", "gateway", "a", 1.0),
            self._link("00000000-0000-0000-0000-000000000002", "a", "b", 1.0),
        ]
        topology = self._topology(nodes, links)
        self.assertEqual({"gateway", "b"}, MODULE.eligible_node_keys(topology))
        cost, reached, selected = MODULE.kruskal_gateway_tree(topology)
        self.assertEqual(0.0, cost)
        self.assertEqual(["gateway"], reached)
        self.assertEqual([], selected)

    # 14
    def test_topology_contract_checks_counts(self):
        topology = self._topology([self._node("gateway")], [])
        self.assertEqual(topology, MODULE.validate_topology_contract(topology))
        broken = dict(topology, nodeCount=2)
        with self.assertRaises(MODULE.VerificationFailure):
            MODULE.validate_topology_contract(broken)

    # 15
    def test_reachability_contract_accepts_valid_shape(self):
        payload = {
            "graphVersion": 1,
            "gatewayKey": "gateway",
            "registeredSensorCount": 0,
            "reachableSensorCount": 0,
            "disconnectedSensorCount": 0,
            "unavailableSensorCount": 0,
            "reachableNodes": [{"nodeKey": "gateway", "hopCount": 0}],
            "unavailableSensorKeys": [],
            "disconnectedComponents": [],
        }
        self.assertEqual(payload, MODULE.validate_reachability_contract(payload))

    # 16
    def test_mst_contract_checks_selected_edge_count(self):
        payload = {
            "graphVersion": 1,
            "gatewayKey": "gateway",
            "totalSimulatedEnergyCost": 0.0,
            "registeredSensorCount": 0,
            "reachableSensorCount": 0,
            "disconnectedSensorCount": 0,
            "unavailableSensorCount": 0,
            "selectedEdgeCount": 0,
            "reachedNodeKeys": ["gateway"],
            "selectedEdges": [],
            "unavailableSensorKeys": [],
            "disconnectedComponents": [],
        }
        self.assertEqual(payload, MODULE.validate_mst_contract(payload))
        broken = dict(payload, selectedEdgeCount=1)
        with self.assertRaises(MODULE.VerificationFailure):
            MODULE.validate_mst_contract(broken)

    # 17
    def test_route_contract_accepts_disconnected_without_path(self):
        payload = {
            "graphVersion": 1,
            "sensorProfileId": "00000000-0000-0000-0000-000000000001",
            "status": "Disconnected",
            "bfs": {"status": "Disconnected", "hopCount": None, "pathToGateway": []},
            "mst": {
                "status": "Disconnected",
                "hopCount": None,
                "accumulatedSimulatedEnergyCost": None,
                "pathToGateway": [],
                "selectedLinkIdsToGateway": [],
            },
        }
        self.assertEqual(payload, MODULE.validate_route_contract(payload))

    # 18
    def test_independent_bfs_matches_api_hop_rows(self):
        nodes = [self._node("gateway"), self._node("a"), self._node("b")]
        links = [
            self._link("00000000-0000-0000-0000-000000000001", "gateway", "a", 1.0),
            self._link("00000000-0000-0000-0000-000000000002", "a", "b", 1.0),
        ]
        topology = self._topology(nodes, links)
        reachability = {
            "graphVersion": 7,
            "gatewayKey": "gateway",
            "registeredSensorCount": 2,
            "reachableSensorCount": 2,
            "disconnectedSensorCount": 0,
            "unavailableSensorCount": 0,
            "reachableNodes": [
                {"nodeKey": "gateway", "hopCount": 0},
                {"nodeKey": "a", "hopCount": 1},
                {"nodeKey": "b", "hopCount": 2},
            ],
            "unavailableSensorKeys": [],
            "disconnectedComponents": [],
        }
        MODULE.assert_api_bfs_matches_snapshot(topology, reachability)

    # 19
    def test_independent_kruskal_matches_prim_summary(self):
        nodes = [self._node("gateway"), self._node("a"), self._node("b")]
        links = [
            self._link("00000000-0000-0000-0000-000000000001", "gateway", "a", 1.0),
            self._link("00000000-0000-0000-0000-000000000002", "a", "b", 1.0),
            self._link("00000000-0000-0000-0000-000000000003", "gateway", "b", 9.0),
        ]
        topology = self._topology(nodes, links)
        mst = {
            "graphVersion": 7,
            "gatewayKey": "gateway",
            "totalSimulatedEnergyCost": 2.0,
            "registeredSensorCount": 2,
            "reachableSensorCount": 2,
            "disconnectedSensorCount": 0,
            "unavailableSensorCount": 0,
            "selectedEdgeCount": 2,
            "reachedNodeKeys": ["gateway", "a", "b"],
            "selectedEdges": [{}, {}],
            "unavailableSensorKeys": [],
            "disconnectedComponents": [],
        }
        proof = MODULE.assert_api_mst_matches_kruskal(topology, mst)
        self.assertEqual(2.0, proof["totalSimulatedEnergyCost"])
        self.assertEqual(2, proof["selectedEdgeCount"])

    # 20
    def test_percentiles_use_nearest_rank(self):
        values = [1.0, 2.0, 3.0, 4.0, 5.0]
        self.assertEqual(3.0, MODULE.percentile_nearest_rank(values, 50))
        self.assertEqual(5.0, MODULE.percentile_nearest_rank(values, 95))

    # 21
    def test_timing_summary_never_claims_threshold(self):
        summary = MODULE.timing_summary([1.0, 2.0, 3.0, 4.0, 5.0])
        self.assertEqual(5, summary["samples"])
        self.assertEqual(3.0, summary["medianMs"])
        self.assertEqual(5.0, summary["p95Ms"])
        self.assertTrue(math.isclose(1.0, summary["minMs"]))
        self.assertNotIn("pass", summary)
        self.assertNotIn("thresholdMs", summary)


if __name__ == "__main__":
    unittest.main(verbosity=2)
