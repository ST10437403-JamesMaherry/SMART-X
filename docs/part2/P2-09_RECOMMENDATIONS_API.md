# P2-09 Recommendation API and real-time refresh contract

The predictive engine is deterministic, **process-local**, and read-only. It
learns only from real `AlertViewed` → associated `SearchSubmitted` → accepted,
state-changing relay commands (P2-09 Commit 1); Commit 2 scores candidates
against **current** P2-06 active issue, device and commanded actuator state.

## HTTP read

`GET /api/realtime/recommendations`

Returns `200 OK`, `Cache-Control: no-store`, and:

- `evaluatedAtUtc`: server UTC evaluation time.
- `minimumSupportingEpisodes`: minimum independent eligible episodes (3).
- `evidenceWindowSeconds`: currently 604800 seconds (7 days).
- `nextRefreshAtUtc`: earliest known recommendation expiry, or `null` when empty.
- `recommendations`: ordered, bounded and empty until genuinely supported.

Each element includes the current source alert episode ID, source identity and
name, condition type/severity/movement, target actuator identity/name, proposed
`RelayOn` or `RelayOff`, supporting episodes, server learning timestamp,
heuristic score (0–100), qualitative confidence band, expiry timestamp and
explanation. The score is **not a calibrated probability**.

No additional execute/suggestion POST route exists. A selected suggestion must
use the existing `POST /api/realtime/devices/{deviceId}/commands` route; the
normal command service revalidates target eligibility and commanded state.

## Client refresh contract for P2-10

Continue to use the existing `/hubs/realtime` SignalR message
`RealtimeChanged`. This remains an *invalidation* only: clients must follow it
with the authoritative recommendations GET and never treat an event as state.

Relevant reasons already propagated by the existing services include:

- `ActuatorCommandRecorded` / `ActuatorCommandUndone` — accepted command or Undo
- `sensor-status-changed`, `sensor-registered`, `sensor-updated` — live eligibility
- `telemetry-changed`, `TelemetryProcessed` — source condition/data changes
- `configuration-changed`, `issue-acknowledged` — active-condition context

Commit 3 adds `OperatorInteractionRecorded` **after** a validated deliberate
search/filter/device/alert POST, including searches with zero results. Failed
POSTs, snapshot GETs, recommendation GETs and other passive reads send no new
interaction invalidation. An unavailable hub cannot roll back recorded events.

After initial connection/reconnection, clients fetch the current HTTP state.
While the panel is visible, P2-10 should also refresh at or before the
`nextRefreshAtUtc` deadline, with a modest bounded periodic fallback especially
when the list is empty; expiry is time-dependent even without new hub messages.

## Runtime boundaries

Stores have bounded local retention; a server process restart clears device,
condition, interaction, pattern and commanded-state simulation data. This is
not a durable audit database, authenticated per-user profile, machine-learning
probability, or automated actuation system.
