"""Dependency-free correctness tests for the P3-11 benchmark harness.

These tests do NOT claim that an HTTP or AVL benchmark ran. They verify the
benchmark oracle and its failure modes before the real API evidence is taken.
Run: python3 -m unittest discover -s scripts/tests -p test_verify_part3_telemetry_load.py -v
"""

from __future__ import annotations

import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

# Import the sibling script without making the repository a Python package.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from verify_part3_telemetry_load import (  # noqa: E402
    Series,
    VerificationError,
    duplicates_for,
    packet,
    parse_utc,
    percentile_summary,
    range_path,
    timestamp_at,
    valid_local_base,
    value_for,
    verify_full_traversal,
    verify_narrow,
    verify_recent_history,
)


class FakeAvlResponseApi:
    """Test double for validating the HARNESS; never used for performance evidence."""

    def __init__(self, series: Series, *, omit_duplicate: bool = False, swap_ties: bool = False):
        self.series = series
        self.rows = []
        for position in range(series.accepted_count):
            at_utc, value = series.expected_row(position)
            row = packet(series.kind, at_utc, value)
            row.update({
                "sensorProfileId": series.sensor_id,
                "deviceIdentifier": series.identifier,
                "telemetryDataType": series.kind,
            })
            sequence = position - series.target if series.target <= position <= series.target + 2 else 0
            self.rows.append((parse_utc(at_utc), sequence, row))
        if omit_duplicate:
            del self.rows[series.target + 1]
        if swap_ties:
            first, second = series.target + 1, series.target + 2
            self.rows[first], self.rows[second] = self.rows[second], self.rows[first]

    def get(self, path: str) -> dict:
        parsed = urlsplit(path)
        self._check_sensor_path(parsed.path)
        query = parse_qs(parsed.query)
        low = parse_utc(query["fromUtc"][0])
        high = parse_utc(query["toUtc"][0])
        limit = int(query["limit"][0])
        cursor = None
        if "afterUtc" in query:
            cursor = (parse_utc(query["afterUtc"][0]), int(query["afterSequence"][0]))
        matches = [
            (time, sequence, row) for time, sequence, row in self.rows
            if low <= time <= high and (cursor is None or (time, sequence) > cursor)
        ]
        page = matches[:limit]
        has_more = len(matches) > limit
        last = page[-1] if page else None
        return {
            "sensorProfileId": self.series.sensor_id,
            "telemetryDataType": self.series.kind,
            "readings": [item[2] for item in page],
            "hasMore": has_more,
            "nextCursor": (
                {"capturedAtUtc": last[2]["capturedAtUtc"], "sequence": last[1]}
                if has_more and last else None
            ),
        }

    def get_array(self, path: str) -> list[dict]:
        self._check_sensor_path(path.split("?", 1)[0])
        result = []
        expected = [
            (timestamp_at(self.series.origin, ordinal), value_for(self.series.kind, ordinal))
            for ordinal in (2, 1, 0)
        ] + [
            (timestamp_at(self.series.origin, self.series.target), value)
            for value in duplicates_for(self.series.kind)
        ]
        for at_utc, value in expected:
            row = packet(self.series.kind, at_utc, value)
            row.update({
                "sensorProfileId": self.series.sensor_id,
                "deviceIdentifier": self.series.identifier,
                "telemetryDataType": self.series.kind,
            })
            result.append(row)
        return result

    def _check_sensor_path(self, path: str) -> None:
        if f"/api/sensors/{self.series.sensor_id}/telemetry" not in path:
            raise AssertionError("Test double received an unexpected sensor endpoint")


class P311TelemetryHarnessTests(unittest.TestCase):
    """Deterministic unit regressions independent of a running API."""

    def series(self, kind: str = "Float") -> Series:
        return Series(
            sensor_id="00000000-0000-0000-0000-000000000031",
            identifier=f"p311-fake-{kind}",
            kind=kind,
            count=24,
            origin=datetime(2026, 10, 1, tzinfo=timezone.utc),
        )

    def test_origin_values_are_timezone_aware_and_offset_equivalent(self) -> None:
        self.assertEqual(parse_utc("2026-10-01T00:00:00.000Z"),
                         parse_utc("2026-10-01T02:00:00+02:00"))
        with self.assertRaises(VerificationError):
            parse_utc("2026-10-01T00:00:00")

    def test_packet_preserves_false_zero_and_one_active_type(self) -> None:
        for kind, value, field in (
            ("Float", 0.0, "floatValue"),
            ("Integer", 0, "integerValue"),
            ("Boolean", False, "booleanValue"),
        ):
            with self.subTest(kind=kind):
                result = packet(kind, "2026-10-01T00:00:00Z", value)
                self.assertEqual(result[field], value)
                self.assertEqual(sum(v is not None for k, v in result.items() if k.endswith("Value")), 1)

    def test_accepted_order_oracle_has_three_equal_timestamps(self) -> None:
        series = self.series()
        target = series.target
        self.assertEqual(series.expected_row(target)[0], series.expected_row(target + 1)[0])
        self.assertEqual(series.expected_row(target)[0], series.expected_row(target + 2)[0])
        self.assertEqual(series.expected_row(target + 3),
                         (timestamp_at(series.origin, target + 1), value_for(series.kind, target + 1)))

    def test_valid_avl_pages_cover_all_types_and_retain_ties(self) -> None:
        for kind in ("Float", "Integer", "Boolean"):
            with self.subTest(kind=kind):
                series = self.series(kind)
                fake = FakeAvlResponseApi(series)
                result = verify_full_traversal(fake, series, 3)
                self.assertEqual(result["indexedReadings"], 26)
                self.assertEqual(verify_narrow(fake, series)["verifiedRows"], 3)
                self.assertTrue(verify_recent_history(fake, series)["originalIngestionOrderPreserved"])

    def test_missing_accepted_duplicate_is_detected(self) -> None:
        series = self.series()
        with self.assertRaises(VerificationError):
            verify_full_traversal(FakeAvlResponseApi(series, omit_duplicate=True), series, 5)

    def test_reordered_equal_time_values_are_detected(self) -> None:
        series = self.series("Integer")
        with self.assertRaises(VerificationError):
            verify_full_traversal(FakeAvlResponseApi(series, swap_ties=True), series, 5)

    def test_partial_cursor_pair_is_never_constructed(self) -> None:
        series = self.series()
        path = range_path(series.sensor_id, "2026-10-01T00:00:00Z", "2026-10-01T01:00:00Z", 2,
                          {"capturedAtUtc": "2026-10-01T00:00:00Z", "sequence": 1})
        params = parse_qs(urlsplit(path).query)
        self.assertEqual(params["afterSequence"], ["1"])
        self.assertIn("afterUtc", params)
        with self.assertRaises(VerificationError):
            range_path(series.sensor_id, "2026-10-01T00:00:00Z", "2026-10-01T01:00:00Z", 2,
                       {"capturedAtUtc": "2026-10-01T00:00:00Z"})

    def test_nearest_rank_p95_and_even_sample_median(self) -> None:
        summary = percentile_summary([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])
        self.assertEqual(summary["p50Ms"], 3.5)
        self.assertEqual(summary["p95Ms"], 6)
        with self.assertRaises(VerificationError):
            percentile_summary([1.0])

    def test_nonlocal_and_embedded_credential_urls_are_rejected(self) -> None:
        for unsafe in (
            "https://api.example.com:443", "http://172.16.0.4:5095",
            "http://user:pass@localhost:5095", "http://localhost:5095/api",
            "http://localhost:5095/?query=1",
        ):
            with self.subTest(url=unsafe), self.assertRaises(VerificationError):
                valid_local_base(unsafe)
        self.assertEqual(valid_local_base("http://127.0.0.1:5095/"), "http://127.0.0.1:5095")


if __name__ == "__main__":
    unittest.main()
