# P3-07: Reproducible greenhouse topology and failure / rerouting verification

**Issue:** `[P3-07] Expose topology APIs and reproducible network simulations`
**Branch:** `feature/part3-topology-api`
**Commit:** `test(integration): simulate real API mesh failure and rerouting`
**Status:** Verification fixture and demonstration commands supplied; record actual local / CI outcomes after execution.

## 1. Assessment goal and source of truth

Prove P3-04 adjacency, P3-05 FIFO BFS, P3-06 priority-queue Prim and
P3-07 HTTP + invalidation integrate as **one** Smart-X network model.
Every sensor in this scenario is registered through `POST /api/sensors`;
all seven weighted links are created through `POST /api/topology/links`.
Operational health changes go through the **existing**
`PATCH /api/sensors/{id}/status` endpoint. Reads use the public topology,
reachability, MST and selected-device route APIs. Tests never seed the
underlying `IMeshTopologyStore` or replace any algorithm service.

Sensor GUIDs and link IDs are assigned by the server **at run time**.
The reproducible parts are the graph shape, names, MAC suffixes, link weights,
operation sequence and expected observable results, **not** hard-coded GUIDs.
This is a logical in-memory simulation, not real radio discovery, an actual
building floor plan, physical energy measurement or actuator control.

## 2. Deterministic hydroponic-greenhouse fixture

All devices are registered as Online initially. The gateway is the existing
logical gateway vertex, not a manually registered sensor.

| Name / label | Category | Function |
| --- | --- | --- |
| Primary irrigation relay (P) | Actuator | Preferred low-cost gateway bridge |
| Backup irrigation relay (B) | Actuator | Higher-cost redundant gateway bridge |
| West-zone moisture probe (M) | Environmental | Must reroute after P fails |
| Central greenhouse repeater (R) | Actuator | Bridges the moisture probe to B |
| East-zone nutrient probe (N) | Environmental | Exercises BFS path versus Prim tree path |
| Single-bridge moisture probe (V) | Environmental | Has no backup and must become disconnected |

Each registration uses a valid unique MAC address with the static prefix
`AA:BB:CC:EE:07:` and suffix `A1` through `A6`. The deployments are in
`Greenhouse / Hydroponics`, with unique zone names.

```text
                  V
                  | 0.50
                  |
    G --1.00-- P --1.00-- M --1.00-- R --1.00-- B --1.25-- N
    |                                           |
    +---------------- 5.00 --------------------+
```

The drawing is schematic, not to scale; the gateway-to-backup edge
closes one redundant cycle. The exact undirected edge set is:

| Link | Relative simulated cost |
| --- | ---: |
| G -- P | 1.00 |
| G -- B | 5.00 |
| P -- M | 1.00 |
| M -- R | 1.00 |
| R -- B | 1.00 |
| B -- N | 1.25 |
| P -- V | 0.50 |

**Initial MST:** select all links except G--B, whose cost is 5.00.
The six selected edges total `1 + 1 + 1 + 1 + 1.25 + 0.50 = 5.75`.

**BFS / Prim distinction:** N has a *two-hop BFS path*
`N -> B -> G` on the configured G--B link. Its *MST tree path* is
`N -> B -> R -> M -> P -> G`, five hops and 5.25 units, because
Prim optimizes the **sum of backbone edges**, not individual route costs.

## 3. Before, failure, and recovery expectations

| Controlled phase | Registered | BFS reachable | Disconnected | Unavailable | Selected MST edges | Total MST cost |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline, all six Online | 6 | 6 | 0 | 0 | 6 | **5.75** |
| Primary P Offline / Fault / Maintenance | 6 | 4 | 1 (V) | 1 (P) | 4 | **8.25** |
| P returns Online | 6 | 6 | 0 | 0 | 6 | **5.75** |
| Delete G--B while P Online | 6 | 6 | 0 | 0 | 6 | **5.75** |
| P Offline after G--B removal | 6 | 0 | 5 | 1 (P) | 0 | **0.00** |
| Recreate G--B with P still Offline | 6 | 4 | 1 (V) | 1 (P) | 4 | **8.25** |

In the failover state, G--B (5.00), B--R (1.00), R--M (1.00),
and B--N (1.25) make the unique gateway-rooted selected tree:
`5 + 1 + 1 + 1.25 = 8.25`.
M's path changes from `M -> P -> G` (two hops, 2.00)
to `M -> R -> B -> G` (three hops, 7.00).
V has only its configured P--V edge; when P is unavailable, V is
**Disconnected**, not secretly rerouted or treated as an unavailable sensor.
The failed relay itself is **Unavailable**; its configured links remain
in the snapshot, but it is ineligible to forward.

After deletion of G--B followed by P failure, the **gateway has no eligible
outgoing edge**. The remaining Online nodes form two disconnected components:
`{B, R, M, N}` and `{V}`. Prim selects no edge and returns zero total cost.
A subsequently recreated G--B link receives a **new** UUID and restores
only those four healthy nodes; V remains disconnected.

A separate regression modifies the G--B cost from 5.00 to 0.25. Prim then
selects G--B and has **total cost 5.00**, while N's fewest-hop BFS path
remains unchanged. The revision advances exactly once for the changed cost.

## 4. Repeatable automated HTTP integration tests

`tests/SmartX.Api.IntegrationTests/Topology/MeshGreenhouseEndToEndScenarioTests.cs`
uses an isolated `WebApplicationFactory<Program>` per test, a real HTTP
client, the real registry, real in-memory adjacency store, BFS and Prim.
It covers eight xUnit executions:

1. Correct real registration metadata, seven configured links, exact MST
   selected costs and BFS-versus-Prim route distinction.
2. Primary P unavailable in each of Offline, Fault and Maintenance states:
   guaranteed B-side fallback for M and disconnection of V.
3. Recovery to the original selected tree without recreating configured edges.
4. Removal of the sole backup gateway link, followed by P failure, splitting
   all five healthy sensors into two disconnected components.
5. Re-adding a backup edge: new link ID, four restored routes, V still isolated.
6. Changing the backup cost to 0.25: tree cost/path changes while BFS stays
   the same minimum hop count.

The tests check graph-version consistency, true HTTP status codes, original
link IDs, defensive prior route responses and no invented route for an
unreachable device.

```bash
# From repository root, run the focused scenario first.
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshGreenhouseEndToEndScenarioTests"

# Run every P3-07 HTTP regression from the feature branch.
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MeshTopologyReadEndpointTests|FullyQualifiedName~MeshTopologySimulationEndpointTests|FullyQualifiedName~MeshTopologyRealtimeInvalidationTests|FullyQualifiedName~MeshGreenhouseEndToEndScenarioTests"
```

## 5. Run and capture a real local demonstration

Start a **fresh Development API** in one terminal, using its existing
HTTP launch profile (port **5203**). The process-local network must
start empty; restarting the API clears the simulated sensor registry.

```bash
dotnet run --project src/SmartX.Api/SmartX.Api.csproj --launch-profile http
```

Then, from a **second terminal in repository root**:

```bash
python3 scripts/demo_part3_greenhouse.py --confirm-simulation --base-url http://localhost:5203 --evidence /tmp/smartx-p3-07-greenhouse-evidence.json
```

The script uses **only the Python standard library**. It restricts the
base URL to `localhost`, `127.0.0.1` or `::1`; refuses non-empty graph state;
and requires `--confirm-simulation` before issuing HTTP mutations.
It prints five validated phases and writes a JSON evidence file **only after
all scenario assertions succeed**. The file includes actual assigned sensor
IDs, graph revisions, route paths and simulated MST cost at each phase.
This evidence contains invented simulation sensor identities and should not
be mistaken for real physical telemetry. Do not paste the generated local
GUIDs into committed static test expectations.

If a host redirects to HTTPS, run the intended local HTTP launch profile.
Do not disable TLS certificate verification to make a remote API demo work.

## 6. Required final verification / PR evidence

```bash
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet restore SmartX.sln
dotnet format SmartX.sln --verify-no-changes --no-restore
git diff --check

cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
cd ../..

python3 scripts/demo_part3_greenhouse.py --help
```

Capture the actual total xUnit count, build/format output, focused P3-07
integration tests, and (optionally) the script's final `PASSED` line and
JSON evidence. **Do not claim successful .NET or live-HTTP execution until
those commands have been run**. The script's results do not validate the
client-side visual dashboard; that is P3-08.

## 7. Scope and security

This test intentionally uses simulation endpoints available in Development/Test.
Other environments reject writes unless explicitly opted in using
`Topology:Simulation:Enabled=true`. **That flag is not authorization**;
never expose these mutating endpoints publicly. SignalR emits lightweight
invalidation hints, while the authoritative GET endpoints remain the source
of truth. No claims are made about hardware operation, persistent topology,
calibrated energy consumption or guaranteed message delivery.
