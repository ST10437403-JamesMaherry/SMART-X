# P3-10 — Current topology insights: API, safety and versioned evaluation

**Scope:** Commit 2 of P3-10. Implements a read-only, bounded server-side projection. **The React presentation belongs to Commit 3.** A green .NET test does not yet prove the alert is displayed in the browser.

## Objective and provenance

The existing Part 2 `/api/realtime/recommendations` endpoint exposes **learned** operator-approved command suggestions. It needs genuine historical alert-view, selected-search and accepted-command evidence; a new or unexpected mesh failure may not match any learned pattern.

This commit adds **a separate source of current operational facts**. `GET /api/topology/insights` uses **the same versioned simulated mesh store and existing BFS** as `/api/topology/reachability`. It does not train a model or infer new links, radio quality or energy use. A never-before-searched disconnection is reported on the first read of a faulty snapshot.

The response explicitly marks every item as `isLearned: false` and identifies the evidence source. This is a **rule-based advisory**, not a probability, prediction or pre-authorized actuator command.

## Endpoint

`GET /api/topology/insights`

- HTTP `200 OK`, `Cache-Control: no-store`; there is no write method, search prerequisite or simulation permission requirement for this read.
- `graphVersion`: authoritative graph revision for the BFS and status evidence.
- `evaluatedAtUtc`: when this HTTP read took place, measured from the server's `TimeProvider`.
- `projectionComputedAtUtc`: when this revision was first projected; it remains unchanged across repeated reads of the same version.
- `registeredSensorCount`, `reachableSensorCount`, `disconnectedSensorCount`, `unavailableSensorCount`: P3-05 routing counts for this revision. `unavailableSensorCount` includes **all** non-forwarding registered sensors, even those with no configured link.
- `insights`: zero to **three** items, in stable priority order. No fabricated identities or cumulative event log.

Each item includes a stable `key`, `kind`, `severity`, `source`, `isLearned`, `isCurrent`, `title`, `explanation`, `recommendedAction`, `evidenceGraphVersion`, `firstObservedAtUtc`, `lastConfirmedAtUtc`, optional `expiresAtUtc`, `affectedRegisteredSensorCount`, `affectedSensorIds` and `additionalAffectedSensorCount`. The IDs are canonical, genuine `SensorProfile.Id` GUIDs rather than UI-generated names. Resolve names using the existing topology snapshot; never make one up. Both the total and truncated count are explicit.

### Deterministic rule set

| Stable key | Condition | Severity | Evidence | Lifecycle |
| --- | --- | --- | --- | --- |
| `topology:gateway-disconnected` | At least one **forwarding-eligible** registered sensor is unreachable from the logical gateway by BFS | `Warning` for one, `Critical` for two or more | `CurrentTopologyBfs` | Active for as long as the condition remains; updates only when the graph version changes |
| `topology:forwarding-unavailable` | A registered sensor has a configured link but its current status makes it ineligible to forward | `Warning` | `CurrentTopologySensorStatus` | Active while linked and unavailable; avoids warnings for ordinary newly registered, unlinked Offline inventory |
| `topology:gateway-restored` | The service previously observed a gateway-disconnected sensor, the new graph has **zero** disconnected eligible sensors, and at least one of those **same registered sensor IDs** is now BFS-reachable | `Info` | `ObservedTopologyTransition` | Visible for 90 seconds; repeated restoration notices are subject to a 60-second cooldown |

**Interpretation safeguards:** Changing a disconnected sensor to `Offline` does not imply its gateway connectivity has recovered. Reconnect messages require a *real previously disconnected sensor* to be in the current BFS reached set. Active failures are **never** suppressed by the recovery cooldown. A recovery notice is not emitted for the first healthy read because no prior failure transition was observed.

## Costs, boundedness and freshness

- **One BFS computation per observed graph revision**, O(V + E) with the existing P3-05 implementation. A SemaphoreSlim serializes projection updates across concurrent readers.
- The endpoint still obtains the current immutable snapshot on **every** request; it does not return a cached response without checking version. Same-version GETs avoid repeating BFS and reuse `projectionComputedAtUtc`.
- At most three alerts are returned. At most **16 registered sensor GUIDs per alert** are serialized; totals and omitted-count fields preserve reporting accuracy.
- The server does not maintain a separate graph, historical search log, pattern store, command store, or event stream for this feature. The projection tracks two active conditions, at most one transient recovery, and a set of disconnected sensor identities bounded by the actual registered graph size (O(V), not an unbounded event log) needed for valid transition detection.
- A warning is **current at the time of the HTTP read**; it is not physical device telemetry freshness. `firstObservedAtUtc` marks the first server evaluation of a continuous condition, **not** the original time the simulated link failed.
- The transient recovery is rechecked for expiry on each GET, even without another mesh revision. Request cancellation is respected before processing a new revision.
- The cache is **process-local**. Restarting the API resets observed transition history; current disconnections still appear on the next read, but recovery notices cannot be reconstructed from the past.

## Read-only and safety boundaries

The new service depends **only** on `IMeshTopologyStore` and `TimeProvider`. It calls the existing `MeshTraversalService.CalculateGatewayRouting` pure BFS projection, and **does not resolve** `IActuatorCommandStore`, `OperatorInteractionService`, `PredictivePatternLearningService`, or `ActuatorCommandService`.

No GET can create a link, change a sensor status, execute/Undo a command, increment learned support or emit a SignalR invalidation. Existing explicit link and status writes still publish their standard invalidations. Commit 3 will use that existing realtime channel to refetch this read-only endpoint.

## Demonstration: real API / manual test (Development only)

Use the *existing* registered-sensor and simulation endpoints to create two online sensors, configure `gateway — relay — leaf`, and read `GET /api/topology/insights`. The healthy response has no warnings. Take the relay Offline through `PATCH /api/sensors/{id}/status`. The first GET now returns two **distinct** current facts: the linked relay is non-forwarding and the online downstream leaf is gateway-disconnected. Repeat the GET: stable keys/first-observation timestamps and unchanged projection timestamp demonstrate no new event on a passive read. Restore the relay: the next GET replaces both warnings with a short-lived recovery notice, and configured links remain intact. The entire journey uses actual registered UUIDs and server-authoritative BFS.

The exact test names are:

- `TopologyInsightServiceTests` (10 deterministic xUnit unit tests, including first visit, expiry, cooldown, payload caps, cancellation, concurrent reads, and restored-vs-offline behaviour).
- `TopologyInsightsEndpointTests` (3 isolated WebApplicationFactory real-HTTP scenarios).

Run from repository root:

```bash
# Restore/build first; dotnet is required on the developer's Mac.
dotnet restore SmartX.sln
dotnet build SmartX.sln -c Release --no-restore

dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj \
  -c Release --no-build --filter 'FullyQualifiedName~TopologyInsightServiceTests'

dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj \
  -c Release --no-build --filter 'FullyQualifiedName~TopologyInsightsEndpointTests'

dotnet test SmartX.sln -c Release --no-build
```

**Evidence status:** Commit 2 source/static checks are not equivalent to executing these tests. Record actual test totals, HTTP payload sizes, and observed performance after running locally; P3-11 will handle formal load measurements. Commit 3 will render these data separately from learned Suggested Actions, and Commit 4 will provide the browser-based first-visit and genuine-pattern-learning demonstrations.
