# P2-09 — Predictive evidence and eligibility regression

## Scope

This commit strengthens the exact acceptance boundaries of P2-09 without adding
another prediction engine, a command endpoint, an operator identity, or fabricated
training events. It preserves the production services from Commits 1–3.

The high-mark predictive requirement is demonstrated by a **real sequence**:
active condition episode -> explicit alert view -> submitted search associating
an actuator -> successful state-changing relay command -> independent pattern
support. The existing P2-04 Undo restores a target for subsequent episodes,
but never trains a new reversal pattern.

## Added regression cases

| Area | New check | Why it matters |
|---|---|---|
| Controlled expiry | Four independently learned episodes decay to three and then two as the seven-day boundary passes; a new genuine episode restores the threshold | Proves support follows current evidence, not a lifetime counter |
| Source eligibility | Decommissioning the source suppresses advice without rewriting historical support | Proves live eligibility is separate from learned evidence |
| Concurrent storage | Readers and writers share the bounded Dictionary safely; no duplicate episode or over-capacity snapshot is exposed | Proves deterministic safe bounded storage under load |
| Unrelated searches | An intervening selection of a different registered actuator prevents stale context from teaching the wrong action | Protects contextual relevance |
| Forged success | A browser POST claiming `CommandExecuted` receives HTTP 400 and teaches nothing | Confirms server-owned command provenance |
| Rejected commands | A command to an Offline target has no interaction/audit success and does not train | Ensures failed actions never count as successes |
| Stale Undo | A second reversal receives HTTP 409 and cannot add support | Protects Undo integrity |
| Competing targets | Two genuinely learned targets appear only while eligible; source recovery removes all advice | Proves predictions respond to current system state |

Unit/store fixtures use an injected `TimeProvider` rather than wall-clock waits.
HTTP tests exercise real sensor registration, the dashboard's HashSet-backed
condition lifecycle, validated interaction endpoints and the actual Stack-backed
command/Undo service. The only test-host substitution is the existing notification
recording stub; recommendation and learning logic remain real.

## Commands (run from repository root)

```bash
dotnet restore SmartX.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
dotnet format SmartX.sln --no-restore --verify-no-changes
dotnet build SmartX.sln --configuration Release --no-restore
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~PredictiveActionServiceTests|FullyQualifiedName~InMemoryInteractionPatternStoreTests"
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~PredictiveRecommendationsEndpointTests"
dotnet test SmartX.sln --configuration Release --no-build
cd src/SmartX.Client && npm run lint && npm run format:check && npm run build && npm run test:e2e
```

The suite deliberately asserts behaviour rather than timing benchmarks or static
hard-coded recommendation responses. A suggestion is **advisory only**; the
existing command service independently revalidates every requested execution.

## Simulation limits

Sensor, command, interaction and pattern stores are process-local. Restarting the
API resets learned patterns. The heuristic confidence band is an explainable
ranking of evidence strength, **not** a probability of correctness, a trained ML
model or a personalized/authenticated operator profile. Recommendation GETs
cannot execute a command, record a search, or increase training support.

The eventual P2-10 demonstration must establish three new genuine alert episodes
before expecting a learned suggestion in a freshly started API process.
