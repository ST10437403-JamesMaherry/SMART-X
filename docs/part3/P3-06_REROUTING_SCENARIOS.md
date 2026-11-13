# P3-06 Commit 3 — Recalculation and self-healing scenario evidence

**Issue:** `[P3-06] Implement Prim MST and automatic mesh rerouting`  
**Branch:** `feature/part3-prim-mst`  
**Commit:** `feat(topology): recompute routes after mesh and sensor failures`

This document describes test fixtures and expected outcomes. **It does not
claim that .NET tests, local format checks or CI have passed.** Record actual
evidence in the pull request after execution on the project toolchain.

## Safety and correctness rules

- Prim computes a **minimum-total-cost gateway-connected backbone** using a
  real .NET min-heap; it does not compute shortest weighted paths per device.
- All alternative radio links must already have been explicitly registered in
  the process-local mesh store; the refresh method is read-only.
- Sensor health comes from the authoritative `ISensorRepository`. Only Online
  and Warning sensors can forward; Offline/Fault/Maintenance/Decommissioned
  sensors cannot. Offline links stay in the configured graph, while the
  P3-04 store removes incident links for decommissioned sensors.
- One operation obtains exactly one immutable, versioned graph snapshot.
  If the version is unchanged, the previous result is reused; otherwise the
  calculation runs Prim plus BFS classification on that same revision.
- A later mutation does not retroactively modify old route results. Refresh
  after the mutation to obtain a newer revision; no background push is
  claimed by this issue.

## Fixture A — Redundant physical-simulation edges

All four links are configured before any status change.

| Link | Cost |
| --- | ---: |
| GW--A | 1 |
| A--L | 1 |
| GW--B | 2 |
| B--L | 3 |

| State | Selected Prim links | Tree total | Leaf route | Leaf route cost | Status(A) |
| --- | --- | ---: | --- | ---: | --- |
| All Online | GW--A, A--L, GW--B | 4 | L → A → GW | 2 | Reachable |
| A Offline | GW--B, B--L | 5 | L → B → GW | 5 | Unavailable |
| A Online again | GW--A, A--L, GW--B | 4 | L → A → GW | 2 | Reachable |

The source mesh still has **four configured links** while A is Offline. The
leaf reroutes because GW--B and B--L already exist. The old route remains
unchanged and the version advances when the registry's sensor status changes.

## Fixture B — No alternative link

Only `GW--A (1)` and `A--L (1)` are configured. After A becomes Fault:

- Tree total drops from `2` to `0` because gateway is isolated.
- A becomes `Unavailable`; L becomes `Disconnected` with no path/cost.
- The two configured links remain recorded, but neither is eligible for
  the gateway tree. A fake fallback connection must **never** be added.

## Fixture C — Cost-only and parent changes

Configured: `GW--A (1)`, `A--B (1)`, `GW--B (5)`.

- Before: Prim picks GW--A and A--B (tree total `2`), so B routes `B → A → GW`.
- Update GW--B cost from `5` to `0.5` through the real store.
- After: Prim picks GW--B and GW--A (tree total `1.5`), so B routes `B → GW`
  with route cost `0.5`. The update retains the original link identity.
- Updating the same link cost to `0.5` again is a no-op and does not bump
  the graph version or force another traversal.

## Fixture D — Distinguishing graph changes from route changes

- A changed **unused** edge may change `GraphVersion` but not selected links
  or any sensor route; `RouteTransitions` remains empty.
- A sensor transition Online → Warning advances the registry-reconciled
  graph revision, but does not make that sensor ineligible for forwarding.
- Two disconnected island components merging/splitting should change
  `HasDisconnectedComponentsChanged` even when no sensor gains a gateway path.
- An isolated newly registered sensor is `Disconnected` after activation;
  it becomes `Reachable` only after an explicit valid gateway link is added.

## Reproducible test commands

Run from the repository root on `feature/part3-prim-mst` after extraction:

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRefreshTests"
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj -c Release --filter "FullyQualifiedName~MeshSpanningTreeRefreshServiceTests"
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MeshPrimReroutingLiveTopologyTests"

dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

## Evidence to capture for the P3-06 PR

Record the actual CLI test counts and GitHub Actions check URL; show the
before/after tree total weights and leaf path changes from executed tests.
Do not claim throughput, reduced battery consumption or actual RF self-healing.
Full MST optimality and cut/cycle adversarial checks remain for **Commit 4**.
