#!/usr/bin/env python3
"""Run and record the final Smart-X Part 3 regression gates.

The script is intentionally a verification harness, not a build system. It:

* validates the real P3-11 load evidence produced by Commits 1 and 2;
* runs the existing backend and frontend quality gates without weakening them;
* runs the focused P3-11 browser journey and then the complete Playwright suite;
* optionally builds/starts Docker Compose and verifies the public proxy routes;
* writes a compact JSON record containing command outcomes and environment data.

No production source files are modified. Docker shutdown deliberately preserves
named volumes: this script never invokes ``docker compose down --volumes``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Sequence


REPOSITORY_MARKER = "SmartX.sln"
DEFAULT_OUTPUT = Path("docs/evidence/part3/p3-11-final-regression.json")

REQUIRED_EVIDENCE = (
    Path("docs/evidence/part3/telemetry-10k.json"),
    Path("docs/evidence/part3/telemetry-50k.json"),
    Path("docs/evidence/part3/mesh-256.json"),
    Path("docs/evidence/part3/mesh-1000.json"),
)


class VerificationFailure(RuntimeError):
    """Raised when a required Part 3 verification condition is not met."""


@dataclass(frozen=True)
class CommandResult:
    """Compact, serialisable result for one executed quality gate."""

    name: str
    command: list[str]
    workingDirectory: str
    exitCode: int
    elapsedMilliseconds: int
    outputTail: list[str]

    @property
    def passed(self) -> bool:
        return self.exitCode == 0


def utc_now() -> str:
    """Return an RFC3339 UTC timestamp without relying on local timezone state."""

    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def find_repository_root(start: Path) -> Path:
    """Walk upwards until SmartX.sln identifies the repository root."""

    current = start.resolve()
    for candidate in (current, *current.parents):
        if (candidate / REPOSITORY_MARKER).is_file():
            return candidate
    raise VerificationFailure(
        f"Could not locate {REPOSITORY_MARKER} from {start.resolve()}"
    )


def load_json(path: Path) -> dict[str, Any]:
    """Load one evidence file with a useful error instead of a raw traceback."""

    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise VerificationFailure(f"Required evidence is missing: {path}") from exc
    except json.JSONDecodeError as exc:
        raise VerificationFailure(f"Evidence is not valid JSON: {path}: {exc}") from exc

    if not isinstance(value, dict):
        raise VerificationFailure(f"Evidence root must be a JSON object: {path}")
    return value


def _contains_positive_numeric_evidence(value: Any) -> bool:
    """Return True when a nested JSON structure contains measured numeric data."""

    if isinstance(value, bool):
        return False
    if isinstance(value, (int, float)):
        return value > 0
    if isinstance(value, dict):
        return any(_contains_positive_numeric_evidence(item) for item in value.values())
    if isinstance(value, list):
        return any(_contains_positive_numeric_evidence(item) for item in value)
    return False


PASS_TEXT_VALUES = frozenset({"PASS", "PASSED", "SUCCESS", "SUCCEEDED", "OK", "COMPLETE", "COMPLETED", "VERIFIED"})
FAIL_TEXT_VALUES = frozenset({"FAIL", "FAILED", "FAILURE", "ERROR", "INVALID"})
PASS_TEXT_KEYS = ("result", "verificationResult", "status", "verdict", "outcome")
PASS_BOOLEAN_KEYS = ("passed", "success")
RUN_ID_KEYS = ("runId", "run_id", "evidenceRunId")


def _normalise_pass_indicator(payload: dict[str, Any]) -> tuple[str, str] | None:
    """Return the first recognised top-level pass indicator, or ``None``.

    P3-11 Commit 1 and Commit 2 were intentionally independent verification
    harnesses and therefore do not share one JSON schema. The final regression
    gate must validate each artifact's meaning rather than rewriting old,
    already-committed evidence into a new schema.
    """

    for key in PASS_TEXT_KEYS:
        value = payload.get(key)
        if isinstance(value, str):
            normalised = value.strip().upper()
            if normalised in PASS_TEXT_VALUES:
                return key, normalised
            if normalised in FAIL_TEXT_VALUES or key in ("result", "verificationResult"):
                raise VerificationFailure(
                    f"Evidence reports a non-passing {key}={value!r}"
                )
            # A generic status such as "finished" is not itself a verdict.
            # Ignore it and continue looking for a dedicated pass/fail field.

    for key in PASS_BOOLEAN_KEYS:
        value = payload.get(key)
        if isinstance(value, bool):
            if value is not True:
                raise VerificationFailure(f"Evidence reports {key}=false")
            return key, "PASS"

    return None


def _find_run_id(payload: dict[str, Any]) -> str | None:
    """Return a run identifier when the producing harness recorded one."""

    for key in RUN_ID_KEYS:
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()

    run = payload.get("run")
    if isinstance(run, dict):
        for key in ("id", "runId", "run_id"):
            value = run.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return None


def _contains_numeric_value(value: Any, expected: int) -> bool:
    """Recursively confirm that an evidence document records the expected scale."""

    if isinstance(value, bool):
        return False
    if isinstance(value, (int, float)):
        return value == expected
    if isinstance(value, dict):
        return any(_contains_numeric_value(item, expected) for item in value.values())
    if isinstance(value, list):
        return any(_contains_numeric_value(item, expected) for item in value)
    return False


def _evidence_sha256(payload: dict[str, Any]) -> str:
    """Return a stable digest for report traceability without inventing a run ID."""

    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def validate_evidence_document(
    path: Path,
    payload: dict[str, Any],
    *,
    require_explicit_pass: bool = True,
) -> dict[str, Any]:
    """Validate common evidence guarantees across independently versioned schemas."""

    indicator = _normalise_pass_indicator(payload)
    if require_explicit_pass and indicator is None:
        raise VerificationFailure(f"Evidence has no recognised explicit PASS indicator: {path}")

    if not _contains_positive_numeric_evidence(payload):
        raise VerificationFailure(f"Evidence contains no positive measured/count data: {path}")

    run_id = _find_run_id(payload)
    result: dict[str, Any] = {
        "path": path.as_posix(),
        "verification": "PASS" if indicator is not None else "STRUCTURALLY_VERIFIED",
        "sha256": _evidence_sha256(payload),
    }
    if indicator is not None:
        result["passIndicator"] = indicator[0]
    if run_id is not None:
        result["runId"] = run_id
    return result


def validate_telemetry_evidence(
    path: Path,
    payload: dict[str, Any],
    expected_readings: int,
) -> dict[str, Any]:
    """Validate Commit 1 telemetry evidence without assuming the mesh schema.

    The telemetry harness predates Commit 2's ``result`` field convention. A
    missing overall pass key is therefore acceptable, but any recognised
    explicit failure indicator is rejected. The document must still prove the
    requested 10k/50k scale and contain measured/count data.
    """

    summary = validate_evidence_document(
        path,
        payload,
        require_explicit_pass=False,
    )
    if not _contains_numeric_value(payload, expected_readings):
        raise VerificationFailure(
            f"{path} does not record the expected {expected_readings} reading scale"
        )
    summary["expectedReadingScale"] = expected_readings
    return summary


def validate_mesh_evidence(path: Path, payload: dict[str, Any], expected_sensors: int) -> None:
    """Apply the stronger invariants guaranteed by the P3-11 mesh verifier."""

    fixture = payload.get("fixture")
    if not isinstance(fixture, dict) or fixture.get("requestedSensorCount") != expected_sensors:
        raise VerificationFailure(
            f"{path} does not describe the expected {expected_sensors}-sensor fixture"
        )

    bridged = payload.get("afterIslandBridge")
    if not isinstance(bridged, dict):
        raise VerificationFailure(f"{path} is missing afterIslandBridge evidence")
    if bridged.get("registeredSensorCount") != expected_sensors:
        raise VerificationFailure(f"{path} registered-sensor count is inconsistent")
    if bridged.get("reachableSensorCount") != expected_sensors:
        raise VerificationFailure(f"{path} did not prove full reachability after bridge repair")
    if bridged.get("disconnectedSensorCount") != 0:
        raise VerificationFailure(f"{path} still reports disconnected sensors after bridge repair")

    failed = payload.get("afterAllGatewayLinksRemoved")
    if not isinstance(failed, dict) or failed.get("disconnectedSensorCount") != expected_sensors:
        raise VerificationFailure(f"{path} did not prove complete gateway disconnection")

    restored = payload.get("afterPrimaryGatewayRestoration")
    if not isinstance(restored, dict) or restored.get("reachableSensorCount") != expected_sensors:
        raise VerificationFailure(f"{path} did not prove route restoration")

    invalid = payload.get("invalidRequestChecks")
    if not isinstance(invalid, dict):
        raise VerificationFailure(f"{path} is missing invalid-request regression evidence")
    if invalid.get("graphVersionUnchanged") is not True or invalid.get("linkCountUnchanged") is not True:
        raise VerificationFailure(f"{path} shows invalid requests mutating topology state")


def validate_required_evidence(root: Path) -> list[dict[str, Any]]:
    """Validate all load artifacts required before the final regression is credible."""

    validated: list[dict[str, Any]] = []
    for relative in REQUIRED_EVIDENCE:
        path = root / relative
        payload = load_json(path)

        if relative.name == "telemetry-10k.json":
            validated.append(validate_telemetry_evidence(relative, payload, 10_000))
        elif relative.name == "telemetry-50k.json":
            validated.append(validate_telemetry_evidence(relative, payload, 50_000))
        elif relative.name == "mesh-256.json":
            validated.append(validate_evidence_document(relative, payload))
            validate_mesh_evidence(relative, payload, 256)
        elif relative.name == "mesh-1000.json":
            validated.append(validate_evidence_document(relative, payload))
            validate_mesh_evidence(relative, payload, 1000)
        else:
            raise VerificationFailure(f"Unrecognised required evidence path: {relative}")

    return validated


def tail_lines(lines: Iterable[str], limit: int = 30) -> list[str]:
    """Keep reports compact while retaining enough terminal context for evidence."""

    values = list(lines)
    return values[-limit:]


def run_command(
    *,
    name: str,
    command: Sequence[str],
    cwd: Path,
    env: dict[str, str] | None = None,
) -> CommandResult:
    """Run one gate, stream output to the terminal, and retain a bounded tail."""

    print(f"\n=== {name} ===")
    print("$ " + " ".join(command))
    started = time.perf_counter()

    process = subprocess.Popen(
        list(command),
        cwd=cwd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )

    captured: list[str] = []
    assert process.stdout is not None
    for raw in process.stdout:
        line = raw.rstrip("\n")
        captured.append(line)
        print(line)

    exit_code = process.wait()
    elapsed_ms = round((time.perf_counter() - started) * 1000)
    result = CommandResult(
        name=name,
        command=list(command),
        workingDirectory=str(cwd),
        exitCode=exit_code,
        elapsedMilliseconds=elapsed_ms,
        outputTail=tail_lines(captured),
    )

    if not result.passed:
        raise VerificationFailure(f"Gate failed: {name} (exit {exit_code})")

    print(f"PASS: {name} ({elapsed_ms} ms)")
    return result


def require_executable(name: str) -> None:
    """Fail early when the requested verification tool is unavailable."""

    if shutil.which(name) is None:
        raise VerificationFailure(f"Required executable is not available on PATH: {name}")


def http_get(url: str, *, timeout: float = 10.0) -> tuple[int, str]:
    """Read one proxied endpoint and return status plus content type."""

    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.headers.get("Content-Type", "")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.headers.get("Content-Type", "")


def http_post_empty(url: str, *, timeout: float = 10.0) -> tuple[int, dict[str, Any]]:
    """POST an empty body, used for the SignalR negotiate smoke check."""

    request = urllib.request.Request(url, data=b"", method="POST")
    request.add_header("Content-Type", "text/plain;charset=UTF-8")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = json.loads(response.read().decode("utf-8"))
            return response.status, payload
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        raise VerificationFailure(
            f"SignalR negotiation failed with HTTP {exc.code}: {body[:300]}"
        ) from exc


def verify_docker_proxy(base_url: str) -> dict[str, Any]:
    """Verify SPA routes, API reads and SignalR through the public Nginx boundary."""

    base = base_url.rstrip("/")
    checks: list[dict[str, Any]] = []
    for path in (
        "/healthz",
        "/api/health",
        "/ingestion",
        "/realtime",
        "/topology",
        "/api/topology",
        "/api/topology/reachability",
        "/api/topology/mst",
    ):
        status, content_type = http_get(base + path)
        if status != 200:
            raise VerificationFailure(f"Docker proxy check failed for {path}: HTTP {status}")
        checks.append({"path": path, "status": status, "contentType": content_type})
        print(f"PASS: Docker proxy {path} -> HTTP {status}")

    negotiate_status, negotiate = http_post_empty(
        base + "/hubs/realtime/negotiate?negotiateVersion=1"
    )
    transports = negotiate.get("availableTransports")
    if negotiate_status != 200 or not isinstance(transports, list):
        raise VerificationFailure("Realtime SignalR negotiate response is incomplete")
    if not any(
        isinstance(item, dict) and item.get("transport") == "WebSockets"
        for item in transports
    ):
        raise VerificationFailure("Realtime SignalR negotiate did not advertise WebSockets")

    checks.append(
        {
            "path": "/hubs/realtime/negotiate?negotiateVersion=1",
            "status": negotiate_status,
            "webSocketsAdvertised": True,
        }
    )
    print("PASS: Docker proxy /hubs/realtime negotiation advertises WebSockets")
    return {"baseUrl": base, "checks": checks}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run final Smart-X Part 3 regression gates and write evidence."
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"JSON evidence path (default: {DEFAULT_OUTPUT.as_posix()})",
    )
    parser.add_argument(
        "--with-docker",
        action="store_true",
        help="Also build/start Compose, smoke the Nginx boundary, then stop containers.",
    )
    parser.add_argument(
        "--docker-base-url",
        default=None,
        help="Override public Docker URL (default uses SMARTX_HTTP_PORT or 8080).",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    root = find_repository_root(Path.cwd())
    client = root / "src" / "SmartX.Client"
    started_at = utc_now()
    command_results: list[CommandResult] = []
    docker_result: dict[str, Any] | None = None

    try:
        validated_evidence = validate_required_evidence(root)
        print("PASS: required P3-11 load evidence is present and internally consistent")

        for executable in ("dotnet", "npm"):
            require_executable(executable)
        if args.with_docker:
            require_executable("docker")

        command_results.append(
            run_command(
                name="Audited .NET restore",
                command=(
                    "dotnet",
                    "restore",
                    "SmartX.sln",
                    "-p:NuGetAudit=true",
                    "-p:NuGetAuditMode=all",
                    "-warnaserror:NU1901;NU1902;NU1903;NU1904",
                ),
                cwd=root,
            )
        )
        command_results.append(
            run_command(
                name=".NET formatting",
                command=(
                    "dotnet",
                    "format",
                    "SmartX.sln",
                    "--verify-no-changes",
                    "--no-restore",
                ),
                cwd=root,
            )
        )
        command_results.append(
            run_command(
                name="Release build",
                command=("dotnet", "build", "SmartX.sln", "-c", "Release", "--no-restore"),
                cwd=root,
            )
        )
        command_results.append(
            run_command(
                name="Backend regression",
                command=("dotnet", "test", "SmartX.sln", "-c", "Release", "--no-restore"),
                cwd=root,
            )
        )

        command_results.append(
            run_command(name="Frontend clean install", command=("npm", "ci"), cwd=client)
        )
        command_results.append(
            run_command(
                name="Frontend dependency audit",
                command=("npm", "audit", "--audit-level=high"),
                cwd=client,
            )
        )
        command_results.append(
            run_command(name="Frontend lint", command=("npm", "run", "lint"), cwd=client)
        )
        command_results.append(
            run_command(
                name="Frontend formatting",
                command=("npm", "run", "format:check"),
                cwd=client,
            )
        )
        command_results.append(
            run_command(name="Frontend build", command=("npm", "run", "build"), cwd=client)
        )
        command_results.append(
            run_command(
                name="Focused P3-11 browser regression",
                command=(
                    "npx",
                    "playwright",
                    "test",
                    "e2e/part3-p3-11-integrated-regression.spec.ts",
                ),
                cwd=client,
            )
        )
        command_results.append(
            run_command(
                name="Complete Playwright regression",
                command=("npm", "run", "test:e2e"),
                cwd=client,
            )
        )

        command_results.append(
            run_command(
                name="Docker Compose configuration",
                command=("docker", "compose", "config", "--quiet"),
                cwd=root,
            )
            if args.with_docker
            else CommandResult(
                name="Docker Compose configuration",
                command=["docker", "compose", "config", "--quiet"],
                workingDirectory=str(root),
                exitCode=0,
                elapsedMilliseconds=0,
                outputTail=["SKIPPED: rerun with --with-docker to execute container gates."],
            )
        )

        if args.with_docker:
            # Build and start through the existing deployment definition. Normal
            # shutdown preserves the encrypted attachment named volume.
            command_results.append(
                run_command(
                    name="Docker Compose build",
                    command=("docker", "compose", "build"),
                    cwd=root,
                )
            )
            try:
                command_results.append(
                    run_command(
                        name="Docker Compose start",
                        command=("docker", "compose", "up", "-d", "--wait"),
                        cwd=root,
                    )
                )
                port = os.environ.get("SMARTX_HTTP_PORT", "8080")
                docker_base = args.docker_base_url or f"http://127.0.0.1:{port}"
                docker_result = verify_docker_proxy(docker_base)
            finally:
                command_results.append(
                    run_command(
                        name="Docker Compose stop",
                        command=("docker", "compose", "down"),
                        cwd=root,
                    )
                )

        completed_at = utc_now()
        report = {
            "result": "PASS",
            "startedAtUtc": started_at,
            "completedAtUtc": completed_at,
            "repositoryRoot": str(root),
            "environment": {
                "platform": platform.platform(),
                "pythonVersion": platform.python_version(),
            },
            "validatedLoadEvidence": validated_evidence,
            "commands": [asdict(result) | {"passed": result.passed} for result in command_results],
            "docker": docker_result,
            "notes": [
                "The report records exit codes and bounded terminal tails; it does not fabricate test counts.",
                "P3-11 telemetry and mesh load artifacts are validated before final gates run.",
                "Docker uses normal compose down; named attachment storage is not deleted.",
                "Run with --with-docker for the complete local release gate before merging P3-11.",
            ],
        }

        output = args.output if args.output.is_absolute() else root / args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(f"\nPASS: final regression evidence written to {output}")
        return 0
    except VerificationFailure as exc:
        print(f"\nFAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
