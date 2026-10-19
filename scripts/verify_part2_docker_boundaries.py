#!/usr/bin/env python3
"""Verify the running Smart-X production HTTP and SignalR boundary.

This is a read-only, standard-library smoke test for the public Nginx origin.
It deliberately does not register sensors, submit telemetry, execute commands,
or read secrets. The existing CI job checks Docker users, health, and private
API port bindings separately.

Usage:
    python3 scripts/verify_part2_docker_boundaries.py \
        --base-url http://127.0.0.1:8080

The script must run only after `docker compose up -d` reports both services
healthy. All checks use the same public origin the browser uses, so a missing
Nginx proxy route fails even when the API itself is healthy internally.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import sys
import time
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen

# Bounded response size prevents an unexpectedly large response from consuming
# arbitrary CI memory. The fresh deployment's detached snapshots are small.
MAX_RESPONSE_BYTES = 4 * 1024 * 1024
MAX_HEADER_BYTES = 32 * 1024
WEBSOCKET_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class SmokeFailure(RuntimeError):
    """An observable deployment contract failed; do not mask it as a pass."""


@dataclass(frozen=True)
class SmokeSettings:
    base_url: str
    timeout_seconds: float

    def __post_init__(self) -> None:
        parsed = urlsplit(self.base_url)
        if (
            parsed.scheme != "http"
            or not parsed.hostname
            or parsed.query
            or parsed.fragment
            or parsed.path not in ("", "/")
        ):
            raise SmokeFailure(
                "--base-url must be a plain HTTP origin, e.g. http://127.0.0.1:8080"
            )
        if not (0.5 <= self.timeout_seconds <= 60):
            raise SmokeFailure("--timeout-seconds must be between 0.5 and 60")

    @property
    def origin(self) -> str:
        return self.base_url.rstrip("/")


def get_response(
    settings: SmokeSettings, path: str, *, post: bool = False
) -> tuple[str, str]:
    """Read one bounded response through Nginx; reject hidden redirects."""
    request = Request(
        settings.origin + path,
        data=b"" if post else None,
        headers={
            "Accept": "application/json" if path.startswith(("/api/", "/hubs/")) else "text/html",
            "Cache-Control": "no-store",
        },
        method="POST" if post else "GET",
    )
    try:
        with urlopen(request, timeout=settings.timeout_seconds) as response:
            if urlsplit(response.url).path != urlsplit(path).path:
                raise SmokeFailure(f"Unexpected redirect for {path}")
            if response.status != 200:
                raise SmokeFailure(f"{path}: HTTP {response.status}, expected 200")
            content_type = response.headers.get("Content-Type", "")
            body = response.read(MAX_RESPONSE_BYTES + 1)
    except HTTPError as error:
        raise SmokeFailure(f"{path}: HTTP {error.code}, expected 200") from error
    except (URLError, TimeoutError) as error:
        raise SmokeFailure(f"{path}: HTTP request failed: {error}") from error

    if len(body) > MAX_RESPONSE_BYTES:
        raise SmokeFailure(f"{path}: response exceeded {MAX_RESPONSE_BYTES} bytes")
    try:
        return body.decode("utf-8"), content_type
    except UnicodeDecodeError as error:
        raise SmokeFailure(f"{path}: response was not UTF-8") from error


def get_json(settings: SmokeSettings, path: str, *, post: bool = False) -> dict[str, Any]:
    body, content_type = get_response(settings, path, post=post)
    if "application/json" not in content_type.lower():
        raise SmokeFailure(f"{path}: expected JSON, got {content_type!r}")
    try:
        payload = json.loads(body)
    except json.JSONDecodeError as error:
        raise SmokeFailure(f"{path}: invalid JSON response") from error
    if not isinstance(payload, dict):
        raise SmokeFailure(f"{path}: expected a JSON object")
    return payload


def check_frontend_route(settings: SmokeSettings) -> None:
    """A direct SPA navigation must return actual React HTML, not an API 404."""
    body, content_type = get_response(settings, "/realtime")
    if "text/html" not in content_type.lower():
        raise SmokeFailure(f"/realtime: expected HTML, got {content_type!r}")
    if 'id="root"' not in body or "Smart-X" not in body:
        raise SmokeFailure("/realtime: production React application shell was missing")
    print("PASS  /realtime loads the production React app directly")


def check_api_snapshots(settings: SmokeSettings) -> None:
    """Verify the DTO shapes for the real Part 2 HTTP endpoints."""
    snapshot = get_json(settings, "/api/realtime/snapshot")
    devices = snapshot.get("devices")
    if not isinstance(devices, list) or type(snapshot.get("deviceCount")) is not int:
        raise SmokeFailure("/api/realtime/snapshot: missing device list or count")
    if snapshot["deviceCount"] != len(devices):
        raise SmokeFailure("/api/realtime/snapshot: device count disagrees with rows")
    if not isinstance(snapshot.get("generatedAtUtc"), str):
        raise SmokeFailure("/api/realtime/snapshot: missing server timestamp")
    if not isinstance(snapshot.get("processing"), dict):
        raise SmokeFailure("/api/realtime/snapshot: missing worker diagnostics")
    print("PASS  /api/realtime/snapshot returns authoritative device and worker data")

    active = get_json(settings, "/api/realtime/snapshot/active-conditions")
    conditions = active.get("activeConditions")
    if not isinstance(conditions, list) or type(active.get("activeConditionCount")) is not int:
        raise SmokeFailure("/api/realtime/snapshot/active-conditions: invalid DTO")
    if active["activeConditionCount"] != len(conditions):
        raise SmokeFailure("/api/realtime/snapshot/active-conditions: mismatched count")
    print("PASS  /api/realtime/snapshot/active-conditions returns one consistent snapshot")

    prediction = get_json(settings, "/api/realtime/recommendations")
    minimum = prediction.get("minimumSupportingEpisodes")
    if (
        not isinstance(prediction.get("recommendations"), list)
        or type(minimum) is not int
        or minimum < 1
        or not isinstance(prediction.get("evaluatedAtUtc"), str)
        or type(prediction.get("evidenceWindowSeconds")) not in (int, float)
        or prediction["evidenceWindowSeconds"] <= 0
    ):
        raise SmokeFailure("/api/realtime/recommendations: invalid prediction snapshot")
    # Never fabricate a recommendation or expect any in a clean deployment.
    print("PASS  /api/realtime/recommendations exposes the read-only P2-09 contract")


def check_worker(settings: SmokeSettings) -> None:
    """Check an actual running worker, not merely the API health endpoint."""
    for attempt in range(20):
        diagnostics = get_json(settings, "/api/realtime/processing")
        status = diagnostics.get("workerStatus")
        if status in ("Faulted", "Stopped"):
            raise SmokeFailure(f"Telemetry worker is {status} rather than active")
        if diagnostics.get("failure") is not None:
            raise SmokeFailure("Telemetry worker reports a processing failure")
        if status in ("Waiting", "Processing"):
            for key in ("acceptedCount", "processedCount", "pendingCount"):
                value = diagnostics.get(key)
                if type(value) is not int or value < 0:
                    raise SmokeFailure(f"/api/realtime/processing: invalid {key}")
            print(f"PASS  Telemetry worker is active ({status}) with valid counters")
            return
        if status != "Starting":
            raise SmokeFailure(f"Unexpected telemetry worker status: {status!r}")
        if attempt < 19:
            time.sleep(0.25)
    raise SmokeFailure("Telemetry worker never left Starting state")


def negotiate_hub(settings: SmokeSettings, hub: str) -> str:
    """A genuine SignalR negotiation must offer WebSockets and a token."""
    payload = get_json(
        settings, f"/hubs/{hub}/negotiate?negotiateVersion=1", post=True
    )
    transports = payload.get("availableTransports")
    if not isinstance(transports, list) or not any(
        isinstance(transport, dict) and transport.get("transport") == "WebSockets"
        for transport in transports
    ):
        raise SmokeFailure(f"/hubs/{hub}: WebSockets transport not offered")
    token = payload.get("connectionToken")
    if not isinstance(token, str) or not token:
        raise SmokeFailure(f"/hubs/{hub}: negotiation omitted connectionToken")
    print(f"PASS  /hubs/{hub} SignalR negotiation offers WebSockets")
    # Never print the one-time connection token to CI logs.
    return token


def check_websocket_upgrade(settings: SmokeSettings, hub: str, token: str) -> None:
    """Prove that Nginx forwards a real WebSocket Upgrade to Kestrel.

    This checks the HTTP 101 response and the RFC 6455 accept signature. It
    does not make a SignalR application connection, and intentionally closes
    the socket immediately after the protocol upgrade. Real client messaging
    and reconnect behaviour are covered by the existing Playwright suite.
    """
    parsed = urlsplit(settings.origin)
    hostname = parsed.hostname
    if hostname is None:
        raise SmokeFailure("Missing base URL hostname")
    port = parsed.port or 80
    host_header = f"[{hostname}]:{port}" if ":" in hostname else f"{hostname}:{port}"
    key = base64.b64encode(os.urandom(16)).decode("ascii")
    expected_accept = base64.b64encode(
        hashlib.sha1((key + WEBSOCKET_MAGIC).encode("ascii")).digest()
    ).decode("ascii")
    path = f"/hubs/{hub}?id={quote(token, safe='')}"
    handshake = (
        f"GET {path} HTTP/1.1\r\n"
        f"Host: {host_header}\r\n"
        "Connection: Upgrade\r\n"
        "Upgrade: websocket\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "\r\n"
    ).encode("ascii")

    try:
        with socket.create_connection((hostname, port), timeout=settings.timeout_seconds) as connection:
            connection.settimeout(settings.timeout_seconds)
            connection.sendall(handshake)
            headers = bytearray()
            while b"\r\n\r\n" not in headers:
                chunk = connection.recv(4096)
                if not chunk:
                    raise SmokeFailure(f"/hubs/{hub}: socket closed during WebSocket upgrade")
                headers.extend(chunk)
                if len(headers) > MAX_HEADER_BYTES:
                    raise SmokeFailure(f"/hubs/{hub}: oversized WebSocket response headers")
    except (OSError, TimeoutError) as error:
        raise SmokeFailure(f"/hubs/{hub}: WebSocket upgrade failed: {error}") from error

    header_lines = headers.split(b"\r\n\r\n", 1)[0].decode("iso-8859-1").split("\r\n")
    if not header_lines[0].startswith("HTTP/1.1 101 "):
        raise SmokeFailure(f"/hubs/{hub}: expected WebSocket HTTP 101, got {header_lines[0]}")

    values: dict[str, str] = {}
    for line in header_lines[1:]:
        if ":" in line:
            name, value = line.split(":", 1)
            values[name.strip().lower()] = value.strip()
    if (
        values.get("upgrade", "").lower() != "websocket"
        or "upgrade" not in values.get("connection", "").lower()
        or values.get("sec-websocket-accept") != expected_accept
    ):
        raise SmokeFailure(f"/hubs/{hub}: invalid WebSocket upgrade headers")
    print(f"PASS  /hubs/{hub} completed a real Nginx-to-Kestrel WebSocket upgrade")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8080")
    parser.add_argument("--timeout-seconds", type=float, default=8.0)
    args = parser.parse_args()
    try:
        settings = SmokeSettings(args.base_url, args.timeout_seconds)
        check_frontend_route(settings)
        check_api_snapshots(settings)
        check_worker(settings)
        for hub in ("dashboard", "realtime"):
            token = negotiate_hub(settings, hub)
            check_websocket_upgrade(settings, hub, token)
    except SmokeFailure as error:
        print(f"FAIL  {error}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as error:
        print(f"FAIL  Invalid network response: {error}", file=sys.stderr)
        return 1
    print("PASS  All Part 2 production routing and SignalR checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
