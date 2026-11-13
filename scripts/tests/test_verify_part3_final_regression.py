#!/usr/bin/env python3
"""Unit tests for the P3-11 final regression evidence validator.

These tests never invoke dotnet, npm or Docker. They isolate the deterministic
validation logic so CI failures identify malformed evidence separately from a
quality-gate command failure.
"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
import sys
from pathlib import Path
from types import ModuleType


SCRIPT = Path(__file__).resolve().parents[1] / "verify_part3_final_regression.py"


def load_module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("verify_part3_final_regression", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


verify = load_module()


def mesh_payload(sensor_count: int) -> dict[str, object]:
    return {
        "result": "PASS",
        "runId": f"mesh-{sensor_count}",
        "fixture": {"requestedSensorCount": sensor_count},
        "afterIslandBridge": {
            "registeredSensorCount": sensor_count,
            "reachableSensorCount": sensor_count,
            "disconnectedSensorCount": 0,
        },
        "afterAllGatewayLinksRemoved": {"disconnectedSensorCount": sensor_count},
        "afterPrimaryGatewayRestoration": {"reachableSensorCount": sensor_count},
        "invalidRequestChecks": {
            "graphVersionUnchanged": True,
            "linkCountUnchanged": True,
        },
        "readTimings": {"topology": {"medianMs": 1.25}},
    }


class EvidenceValidationTests(unittest.TestCase):
    def test_common_evidence_accepts_pass_with_run_id_and_numeric_measurement(self) -> None:
        payload = {
            "result": "PASS",
            "runId": "abc123",
            "observed": {"count": 10_000},
        }
        result = verify.validate_evidence_document(Path("telemetry.json"), payload)
        self.assertEqual("PASS", result["verification"])
        self.assertEqual("result", result["passIndicator"])
        self.assertEqual("abc123", result["runId"])
        self.assertEqual(64, len(result["sha256"]))

    def test_common_evidence_accepts_status_pass_variant(self) -> None:
        payload = {"status": "passed", "run_id": "telemetry-run", "count": 10_000}
        result = verify.validate_evidence_document(Path("telemetry.json"), payload)
        self.assertEqual("PASS", result["verification"])
        self.assertEqual("status", result["passIndicator"])
        self.assertEqual("telemetry-run", result["runId"])

    def test_telemetry_evidence_accepts_commit1_shape_without_result_field(self) -> None:
        payload = {
            "runId": "telemetry-10k",
            "requestedReadings": 10_000,
            "rangeQueries": {"samples": 25, "medianMs": 1.2},
            "avlChecks": {"height": 14},
        }
        result = verify.validate_telemetry_evidence(
            Path("telemetry-10k.json"), payload, 10_000
        )
        self.assertEqual("STRUCTURALLY_VERIFIED", result["verification"])
        self.assertEqual(10_000, result["expectedReadingScale"])

    def test_telemetry_evidence_rejects_explicit_failure(self) -> None:
        payload = {
            "status": "FAIL",
            "runId": "bad-run",
            "requestedReadings": 10_000,
        }
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_telemetry_evidence(
                Path("telemetry-10k.json"), payload, 10_000
            )

    def test_telemetry_evidence_requires_expected_scale(self) -> None:
        payload = {
            "runId": "wrong-scale",
            "requestedReadings": 9_999,
            "rangeQueries": {"samples": 25},
        }
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_telemetry_evidence(
                Path("telemetry-10k.json"), payload, 10_000
            )

    def test_common_evidence_rejects_non_pass_result(self) -> None:
        payload = {"result": "FAIL", "runId": "abc", "count": 1}
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_evidence_document(Path("bad.json"), payload)

    def test_common_evidence_allows_missing_run_id_but_records_digest(self) -> None:
        payload = {"result": "PASS", "count": 1}
        result = verify.validate_evidence_document(Path("evidence.json"), payload)
        self.assertNotIn("runId", result)
        self.assertEqual(64, len(result["sha256"]))

    def test_common_evidence_rejects_documents_without_numeric_evidence(self) -> None:
        payload = {"result": "PASS", "runId": "abc", "notes": ["text only"]}
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_evidence_document(Path("bad.json"), payload)

    def test_mesh_evidence_accepts_complete_repair_failure_and_restore_cycle(self) -> None:
        verify.validate_mesh_evidence(Path("mesh-256.json"), mesh_payload(256), 256)

    def test_mesh_evidence_rejects_wrong_fixture_size(self) -> None:
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_mesh_evidence(Path("mesh.json"), mesh_payload(256), 1000)

    def test_mesh_evidence_requires_every_sensor_reachable_after_bridge(self) -> None:
        payload = mesh_payload(256)
        bridged = payload["afterIslandBridge"]
        assert isinstance(bridged, dict)
        bridged["reachableSensorCount"] = 255
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_mesh_evidence(Path("mesh.json"), payload, 256)

    def test_mesh_evidence_requires_complete_disconnection_proof(self) -> None:
        payload = mesh_payload(256)
        failed = payload["afterAllGatewayLinksRemoved"]
        assert isinstance(failed, dict)
        failed["disconnectedSensorCount"] = 255
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_mesh_evidence(Path("mesh.json"), payload, 256)

    def test_mesh_evidence_requires_restoration_proof(self) -> None:
        payload = mesh_payload(256)
        restored = payload["afterPrimaryGatewayRestoration"]
        assert isinstance(restored, dict)
        restored["reachableSensorCount"] = 0
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_mesh_evidence(Path("mesh.json"), payload, 256)

    def test_mesh_evidence_requires_invalid_requests_to_be_non_mutating(self) -> None:
        payload = mesh_payload(256)
        invalid = payload["invalidRequestChecks"]
        assert isinstance(invalid, dict)
        invalid["graphVersionUnchanged"] = False
        with self.assertRaises(verify.VerificationFailure):
            verify.validate_mesh_evidence(Path("mesh.json"), payload, 256)

    def test_load_json_rejects_non_object_root(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "evidence.json"
            path.write_text(json.dumps(["PASS"]), encoding="utf-8")
            with self.assertRaises(verify.VerificationFailure):
                verify.load_json(path)

    def test_tail_lines_keeps_only_requested_suffix(self) -> None:
        self.assertEqual(["3", "4"], verify.tail_lines(["1", "2", "3", "4"], 2))


if __name__ == "__main__":
    unittest.main(verbosity=2)
