# P3-04 — Mesh graph correctness and concurrency verification

**Assessment area:** graph representation, validated topology and safe graph mutation (foundation for BFS/Prim).

**Source baseline:** `dev` commit `903390efb8557a9c70777dc8d96fd7bca8bd126d` (P3-03 recovered into `dev`), followed by P3-04 Commits 1–3 on `feature/part3-mesh-graph`.

This file records **test intentions and reproducible commands**, not a claim that CI ran or that simulated links represent discovered physical connections. Paste the actual local/CI test summaries into the PR when validation completes.

## Graph invariants

| Invariant | Where enforced | How it is verified |
| --- | --- | --- |
| Exactly one gateway, even with no sensors | `MeshNodeKey`, `MeshGraphSnapshot`, mesh store | Gateway-only, empty/isolated and dense topology fixtures |
| Sensor keys are genuine registered profile GUIDs | `MeshNode.FromSensor`, `ISensorRepository` reconciliation | Real in-memory registry; unknown-ID and removed-profile regression |
| Registration does not invent a link | `ReconcileSensorInventory` | Concurrent registration and disconnected-components fixtures |
| One undirected link is present in both adjacency dictionaries | `AddLinkAsync`, `RemoveLinkCore` | Independent snapshot-to-neighbour oracle and parallel add/remove/re-add |
| Link IDs and unordered endpoint pairs are unique | `AddLinkAsync`, `MeshGraphSnapshot` | Parallel same-ID and reversed-pair collision tests |
| Costs are strictly positive and finite | `MeshLink`, `UpdateLinkCostAsync` | Zero, negative, NaN and infinite weight rejection; concurrent updates |
| No dangling/decommissioned incident links | Registry reconciliation and snapshot validation | High-degree decommission and scripted registry deletion |
| Offline/Fault/Maintenance remain distinct from absent vertices | `MeshNode.IsForwardingEligible` | Real sensor status transition tests, while keeping configured links |
| Reads are sorted, immutable revisions | `MeshGraphSnapshot`, incident-link sorting | Dense graph ordering, read-only views and concurrent update snapshots |
| Every accepted change increments a monotonic revision | `AdvanceVersion` | Exact version deltas after independent adds, cost changes and churn |
| Cancelled queued work cannot mutate graph state | `SemaphoreSlim` gate | Deterministic blocked registry read plus cancelled pending writer |

## Test files

Commit 3 adds **25 cases** (18 adversarial and 7 concurrency) in two new xUnit classes, supported by a shared test fixture.

- `tests/SmartX.Domain.Tests/Topology/MeshDomainModelTests.cs` — immutable identities, link costs, canonical endpoints, snapshot validation and status eligibility (Commit 1).
- `tests/SmartX.Infrastructure.Tests/Topology/InMemoryMeshTopologyStoreTests.cs` — normal CRUD, symmetric edges, stable order, version semantics, sensor lifecycle and cancellation (Commit 2).
- `tests/SmartX.Infrastructure.Tests/Topology/MeshTopologyAdversarialTests.cs` — disconnected islands and isolated vertices; a complete 10-vertex/45-link graph; duplicate ID rejection; stale-index checks after removal/recreation; decommissioning a high-degree vertex without damaging unrelated links; malformed registry, missing identities and invalid costs (Commit 3).
- `tests/SmartX.Infrastructure.Tests/Topology/MeshTopologyConcurrencyTests.cs` — 48 independent parallel writes; 64 mirrored-edge collision attempts; 24 shared-ID attempts; simultaneous cost updates with reader snapshots; interleaved add/remove/re-add; concurrent real sensor registration; cancellation during a blocked registry read (Commit 3).
- `tests/SmartX.Infrastructure.Tests/Topology/MeshTopologyTestSupport.cs` — repeatable sensor fixtures, ID assignment and independent structural/adjacency assertions (Commit 3).

Concurrency tests use `Task.Run` and asynchronous yields to exercise real contention. Assertions concern **correctness, not wall-clock speed**. Tests do not assume that two distinct reads return the same version while writes are active. For any quiescent graph, the independent oracle reconstructs each vertex's expected neighbours from the published edge list and verifies both directions.

## Run these checks from the repository root

```bash
# Focused domain invariants
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~MeshDomainModelTests"

# Full P3-04 infrastructure suite (existing tests plus the new tests)
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~InMemoryMeshTopologyStoreTests|FullyQualifiedName~MeshTopologyAdversarialTests|FullyQualifiedName~MeshTopologyConcurrencyTests"

# Final full-solution regression
dotnet build SmartX.sln -c Release
dotnet test SmartX.sln -c Release
dotnet format SmartX.sln --verify-no-changes
git diff --check
```

Store the **actual** test totals, relevant CI run URL and `git log -3 --oneline` in the P3-04 PR evidence. Do not fabricate timings or benchmark gains. The graph's cost values are relative **simulated** routing weights, not calibrated joules or measured radio telemetry.

## Complexity and boundaries

The adjacency representation is `Dictionary<MeshNodeKey, Dictionary<MeshNodeKey, MeshLink>>`, with a separate canonical `Dictionary<Guid, MeshLink>` index. Space is **O(V + E)**, including both directions of each undirected edge. A link add/remove/cost update is expected **O(1)** for dictionary operations **after** the authoritative registry reconciliation; reconciliation scans `V` registered sensors and can remove incident edges for disappeared or decommissioned sensors. Ordering a snapshot costs **O(V log V + E log E)** and ordering one neighbour list costs **O(d log d)** for degree `d`. These are complexity bounds, not a speed benchmark.

The store is process-local and isolated to a single API process. Its graph synchronization does not make the independent sensor registry transactional; the next mesh operation reconciles the most recent returned registry view. Decommissioning is terminal; Offline/Fault/Maintenance vertices can remain configured but are **not eligible to forward**.

**Next issue:** P3-05 will compute gateway reachability and fewest-hop routes using BFS over immutable graph snapshots, with explicit handling of disconnected and non-forwarding vertices. P3-06 will compute weighted spanning trees/forests using Prim's algorithm. P3-04 intentionally implements neither algorithm and does not infer routes from visual graph proximity.
