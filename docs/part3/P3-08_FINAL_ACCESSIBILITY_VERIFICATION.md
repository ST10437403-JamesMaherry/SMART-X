# P3-08 Commit 4 - Accessible Mesh Dashboard Verification

## Verification objective

Prove that the P3-08 dashboard is **an accessible view of the real Smart-X
backend**, not a browser-only drawing or a client-computed routing engine.
This commit adds a final browser regression suite; it does **not** change the
validated P3-04 graph, P3-05 BFS, P3-06 Prim, or P3-07 API implementations.

All automated scenarios use the Development API started by Playwright at
`http://127.0.0.1:5095`. Devices are registered by HTTP; links and device
health are changed using the real simulation endpoints. Randomized registration
identifiers isolate tests even though the test database/topology is process-local
and shared within a Playwright run. The test suite uses one worker.

## New regression suite

File: `src/SmartX.Client/e2e/part3-topology-final-regression.spec.ts`

Playwright group: `P3-08 final accessible topology and rerouting regression`

| Scenario | Operator-visible proof | Underlying backend proof |
| --- | --- | --- |
| Keyboard and semantics | Space/Enter activate table/graph controls and nodes, `aria-pressed` follows selection, both legends and textual BFS steps remain available | Real registered sensor and gateway link |
| Redundant primary-relay failure | Selected Leaf remains selected, Prim tree-route cost changes from 2.00 to 5.00, backup links become highlighted, recovered path returns to 2.00 without manual refresh | Real `PATCH /api/sensors/{id}/status` changes Prim's tree and graph revision; route GET confirms accumulated cost |
| Single bridge fails | Selected Leaf displays `Disconnected` with no BFS path; both configured edges remain visible but are ineligible; Warning status restores the path | Actual graph still contains both links; actual route GET returns `Disconnected` while relay is in Maintenance |
| Missed event on route unmount | Navigation to Ingestion and browser Back preserve the shared sensor ID; returning to Topology displays the new unavailable status | Health changed through the API while Topology's realtime subscription was unmounted |
| 375px viewport | No **document** horizontal overflow; fixed-size diagram scrolls inside its own named region; table and controls remain usable | Same authoritative projection in Graph and Table modes |
| Large registry | Diagram stays bounded to 25 sensors, table pages through all 27 matching registrations, and a selected sensor beyond the first 25 becomes visible in the diagram | 27 unique sensor registrations made through real HTTP calls |

### Exact deterministic rerouting fixture

Create three Online sensors: Primary, Backup and Leaf.

| Link | Simulated relative cost |
| --- | ---: |
| Gateway - Primary | 1.00 |
| Primary - Leaf | 1.00 |
| Gateway - Backup | 4.00 |
| Backup - Leaf | 1.00 |

The **selected Leaf's MST tree-route cost** is `2.00` while Primary is Online.
When Primary becomes Offline, its configured edges remain stored but cannot
forward. The selected route changes to Leaf -> Backup -> Gateway with a cost
of `5.00`. Restoring Primary to Online returns Leaf's selected tree route cost
to `2.00`. The test checks the **selected route**, not an assumed whole-network
MST total: other tests may have registered devices attached to the shared
gateway in the same Playwright process.

BFS still has a two-hop route in the redundant fixture; the test must never
mislabel the MST backbone as the individually cheapest weighted path.

### Single-bridge fixture

Gateway -> Relay (1.00) -> Leaf (1.00), without a backup link. When Relay is
placed in Maintenance, Leaf remains forwarding-eligible but cannot reach the
gateway, therefore it is **Disconnected**. The relay itself is **Unavailable**.
The backend retains the configured links. Returning the relay to Warning
makes it forwarding-eligible and restores the original BFS path.

## Local acceptance commands

From the repository root after extracting Commit 4:

```bash
cd src/SmartX.Client

# Format the complete new regression file using the repository's pinned Prettier.
npx prettier --write e2e/part3-topology-final-regression.spec.ts

npm run lint
npm run format:check
npm run build

# Focused final-issue acceptance.
CI=true npm run test:e2e -- --grep 'P3-08 final accessible topology and rerouting regression' --retries=0

# Earlier P3-08 contracts must remain green.
CI=true npm run test:e2e -- --grep 'P3-08 real API mesh routing overlays and simulation controls' --retries=0
CI=true npm run test:e2e -- --grep 'P3-08 accessible interactive mesh visualization' --retries=0
CI=true npm run test:e2e -- --grep 'Part 3 live topology API foundation' --retries=0
CI=true npm run test:e2e -- --grep 'P3-01 integrated three-pillar navigation' --retries=0

# Full browser regression is recommended before the P3-08 PR.
CI=true npm run test:e2e -- --retries=0

cd ../..
git diff --check
```

The Playwright configuration starts the actual ASP.NET Core API and Vite client.
Do not run another API on its reserved port `5095` during these tests. The
client tests rely on the process-local store and safe Development simulation
mode; do not run them against Production or physical hardware.

For a full integration gate, run from the repository root:

```bash
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check
```

The **actual numeric test counts, GitHub Actions results and screenshots must
come from the local run**. This document records expected assertions, not
invented execution evidence.

## Accessibility and UX evidence checklist

Capture genuine application evidence, without using mock topology data:

1. **Graph / keyboard:** the primary dashboard with the Graph view focused,
   native sensor button focused, pressed state and a text legend for status and
   line style.
2. **Routing:** selected Leaf details showing its BFS route and separate MST
   accumulated tree cost. The overlay must identify Prim as a minimum-total-cost
   backbone, not the shortest individual weighted path.
3. **Failure:** the same Leaf after Primary goes Offline; backup path highlighted
   and changed route cost. Preserve the selected sensor and chosen overlay.
4. **No backup:** single bridge in Maintenance; Leaf explicitly Disconnected,
   relay Unavailable, both configured edges still present as ineligible.
5. **Responsive:** a 375px viewport with the dashboard, Graph/Table toggle,
   named internal scroll region and usable simulation controls. Verify the
   document itself has no horizontal overflow.
6. **Scale:** 27 matching registered sensors, 25-node bounded diagram, all 27
   accessible through the paginated table, and a selected item on page two.
7. **Cross-pillar:** navigate to Ingestion and back; confirm shared selection
   and a fresh authoritative route after any missed event.

Native HTML buttons, named form controls, `aria-pressed`, `role=status/alert`,
semantic tables, text route steps, and shape/line-pattern legends are present.
These are explicit accessibility affordances, **not a claim of WCAG conformance
or an automated screen-reader certification**. A human should additionally
check focus order, zoom, a screen reader, contrast, and touch-target comfort.

## Realtime reconnect: tested versus manual boundary

Automated tests verify live SignalR invalidation while Topology is mounted,
and refetch after a missed change when leaving/returning to the Topology route.
The latter exercises a **new hub connection**, not a forced transport-level
`onreconnected` callback.

For a true transport interruption, verify manually in a local Development demo:

1. Open Topology and wait until the status says `Realtime channel: connected`.
2. Select an Online sensor with a configured gateway route.
3. Temporarily set the **browser** offline in DevTools, leaving the API process
   running. Change the sensor status through a separate direct HTTP request
   or a second connected browser. Do not change the client graph by hand.
4. Return the original browser online, wait for SignalR to reconnect, and
   confirm the dashboard refetches authoritative topology, BFS and MST data
   without clicking Refresh, while retaining the selected sensor.
5. Restore the status after capturing evidence.

Do not mark step 4 as proven solely from the navigation/remount regression.

## Scope boundaries and hand-off

- Graph positions are deterministic **logical** layout coordinates, not sensor
  geography or radio propagation estimates.
- Energy weights are simulated relative costs, not measured Joules or battery
  life savings.
- MST minimizes the total selected backbone cost. BFS minimizes hop count.
  Neither implements Dijkstra shortest weighted paths.
- All topology state remains process-local; an API restart clears the
  simulated configuration.
- P3-09 adds per-device **AVL-backed historical telemetry**. Do not implement
  a second telemetry history store or fake historical readings here.
- P3-08 closes only after the local checks and the GitHub PR CI gate succeed.
