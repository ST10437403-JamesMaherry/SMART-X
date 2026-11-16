"""Regression checks for the local-only greenhouse scenario fixture (stdlib)."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock


SOURCE = Path(__file__).resolve().parents[1] / "load_mesh_scenario.py"
spec = importlib.util.spec_from_file_location("mesh_scenario", SOURCE)
module = importlib.util.module_from_spec(spec)
import sys
sys.modules[spec.name] = module
spec.loader.exec_module(module)


class FixtureChecks(unittest.TestCase):
    def test_loopback_origin_is_required(self):
        for url in (
            "https://localhost:8080",
            "http://example.com:8080",
            "http://192.168.1.10:8080",
            "http://localhost:8080/api",
            "http://user:pass@localhost:8080",
        ):
            with self.subTest(url=url), self.assertRaises(module.ScenarioError):
                module.Api(url)
        self.assertEqual(module.Api("http://127.0.0.1:8080").base,
                         "http://127.0.0.1:8080")

    def test_fixture_identifiers_and_edges_are_unique(self):
        ids = [module.identifier(device) for device in module.DEVICES.values()]
        self.assertEqual(len(ids), 6)
        self.assertEqual(len(set(ids)), 6)
        self.assertEqual(len(module.EDGES), 7)
        self.assertEqual(len({
            module.pair(a, b) for a, b, _ in module.EDGES
        }), 7)
        self.assertTrue(all(cost > 0 for _, _, cost in module.EDGES))

    def test_refuses_non_fixture_sensor_before_writing(self):
        api = Mock()
        api.get.return_value = [{
            "deviceIdentifier": "AA:00:00:00:00:01",
            "id": "00000000-0000-0000-0000-000000000001",
        }]
        with self.assertRaisesRegex(module.ScenarioError, "non-demo sensors"):
            module.inspect_registry(api)
        api.post.assert_not_called()

    def test_existing_fixture_metadata_must_match(self):
        api = Mock()
        api.get.return_value = [{
            "deviceIdentifier": module.identifier(module.DEVICES["primary"]),
            "name": "Wrong name",
            "category": "Actuator",
            "identifierKind": "MacAddress",
            "telemetryDataType": "Float",
        }]
        with self.assertRaisesRegex(module.ScenarioError, "does not match"):
            module.inspect_registry(api)

    def test_known_expectations_match_original_integration_suite(self):
        self.assertEqual(module.EXPECTED["healthy"], (6, 0, 0, 5.75))
        self.assertEqual(module.EXPECTED["relay-failure"], (4, 1, 1, 8.25))
        self.assertEqual(module.EXPECTED["island"], (0, 5, 1, 0.0))
        self.assertEqual(module.EXPECTED["recovered"], (6, 0, 0, 5.75))


if __name__ == "__main__":
    unittest.main()
