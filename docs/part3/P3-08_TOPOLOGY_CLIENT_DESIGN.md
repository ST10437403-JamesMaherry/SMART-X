# P3-08 — Accessible Interactive Mesh Dashboard

## Source of truth and issue scope

P3-08 presents the real P3-07 adjacency graph, BFS reachability and Prim minimum
spanning tree (MST) through React + TypeScript. All registered identities,
statuses, weighted links and routes originate in the API. The client does not
register synthetic devices, derive radio coverage from positions or create
unconfigured edges.

The API data loader (`useTopologyData` + `loadConsistentTopology`) fetches graph,
reachability, MST, and selected-route projections, and publishes them only when
graph revisions agree. The application-wide `WorkspaceStateProvider` owns the
selected sensor; switching pillars does not create a second selection store.

## Commit 1 — Typed API client and refresh state

- `src/SmartX.Client/src/api/topologyApi.ts` contains typed GET contracts.
- `src/SmartX.Client/src/features/topology/useTopologyData.ts` cancels obsolete
  requests, retries inconsistent revisions, and refetches after relevant
  SignalR invalidation or reconnection.
- `src/SmartX.Client/src/pages/TopologyPage.tsx` preserves the existing
  three-pillar navigation and shows authoritative summary, loading and errors.
- Existing P3-01 navigation browser tests remain part of the acceptance gate.

## Commit 2 — Logical SVG diagram and accessible table

Files:

- `src/SmartX.Client/src/features/topology/meshLayout.ts` — deterministic,
  bounded positions and exact filtered-link membership.
- `src/SmartX.Client/src/features/topology/MeshCanvas.tsx` — SVG vertices,
  configured edges and actual simulated link weights; native HTML buttons
  layered over visible sensor glyphs for keyboard interaction.
- `src/SmartX.Client/src/features/topology/TopologyWorkspace.tsx` — name,
  identifier, MAC, UUID and category search, Graph/Table view controls,
  a semantic paginated table, and shared sensor selection.
- `src/SmartX.Client/src/features/topology/TopologyNodeDetails.tsx` — real
  identity/status/zone, BFS hop count and ordered route steps, MST membership,
  selected-tree accumulated route cost and configured neighbor weights.
- `src/SmartX.Client/src/features/topology/TopologyWorkspace.css` — near-black,
  high-contrast responsive layout with visible focus states and internal
  diagram scrolling instead of page-level overflow.

### Rendering bounds

The diagram intentionally draws at most **25 registered sensors plus the
logical gateway** per viewport. Vertices are ordered by authoritative BFS hop
level and canonical key, then assigned fixed grid positions. This is a
readability/layout technique, not a second routing algorithm; no edges are
inferred from apparent proximity. When the displayed subset omits a device,
the diagram explicitly states the number omitted and draws **only** configured
links whose endpoints are both visible.

The accessible HTML table is paginated at **20 matching sensors per page**.
Search filters genuine registrations and the active selection remains visible
in the diagram when it matches the query. The text alternative and selection
controls remain available on mobile. At widths around 375px, any horizontal
scrolling is contained within the diagram/table regions, not the document.

### Connectivity is explicit, not color-only

Node status is supplied as text. `Reachable` means forwarding eligible and
connected to the gateway in the authoritative BFS projection; `Disconnected`
means eligible but without a gateway path; `Unavailable` indicates an
ineligible operational status. Different line styles and explicit labels
supplement color. The logical gateway is not a physical location on a map.

The selected device panel displays two distinct route concepts:

- **BFS:** fewest-hop sensor-to-gateway path.
- **Prim:** route over the minimum-total-cost selected tree and its accumulated
  relative cost. The MST does **not** promise the individually least-cost path.

Costs are simulated positive relative weights, not measured Joules or battery
savings. No link edits, scenario state or command effects are implemented in
this commit.

## Planned next commits

- **Commit 3:** Add explicit all-connections/BFS/MST visual overlays, visible
  failure/unreachable summaries, and API-backed simulation controls.
- **Commit 4:** Verify routing selection, keyboard, resize, failure/recovery and
  realtime reconnect with independent Playwright coverage.

## Local verification commands

From the repository root:

```bash
cd src/SmartX.Client
npm ci
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep "P3-08 accessible interactive mesh visualization" --retries=0
CI=true npm run test:e2e -- --grep "Part 3 live topology API foundation" --retries=0
CI=true npm run test:e2e -- --grep "P3-01 integrated three-pillar navigation" --retries=0
cd ../..
git diff --check
```

The Playwright tests exercise real registration, online status and link-create
HTTP requests against the isolated .NET host. They verify configured edges,
selected details, search by the actual physical identifier, table navigation,
keyboard selection, 375px width and the 25-node diagram bound. A green outcome
must be recorded from a **local execution**; this document makes no claim that
these new tests have already passed.

## Commit 3 — Server-backed overlays and simulation operations

The overlay buttons show **all configured connections**, a **selected sensor's
BFS fewest-hop path**, or **Prim's gateway-rooted minimum-total-cost backbone**.
They are not three client-side routing algorithms. `meshOverlay.ts` consumes the
version-consistent server projections from Commit 1:

- In **All configured connections** mode, every visible configured link is
  displayed. Dashed links adjacent to unavailable sensors are not represented
  as currently forwarding links. The backend preserves these configured links.
- In **BFS** mode, the exact ordered `selectedRoute.bfs.pathToGateway` is mapped
  to *existing undirected link IDs* in that same graph revision. If no sensor
  is selected or its status is `Unavailable`/`Disconnected`, no route is
  highlighted and a text explanation is displayed. This mapping is not BFS.
- In **Prim** mode, only `mst.selectedEdges[].linkId` entries actually present
  in the same topology snapshot are highlighted. The total displayed cost is
  the server's total **backbone** cost, not a Dijkstra path cost. A separate
  selected-device details panel still exposes its individual MST tree route.

Other configured links remain faint for context in BFS/Prim modes, while
links touching unavailable endpoints are also explicitly dashed; strokes,
line dashes, button labels, `aria-pressed`, and a readable status paragraph
communicate what is selected without relying on color alone. A 25-sensor
maximum still applies. Highlights whose endpoints are outside the diagram are
counted as omitted in text, not drawn as invented connections. The complete
paginated semantic table remains available for larger networks.

### Simulation controls and failure communication

`TopologySimulationControls.tsx` exposes only **real API writes**:

| Action | Authoritative HTTP operation |
| --- | --- |
| Create a weighted undirected link | `POST /api/topology/links` |
| Change a selected link's relative cost | `PATCH /api/topology/links/{id}/cost` |
| Remove a configured link | `DELETE /api/topology/links/{id}` |
| Change selected registered sensor's simulated health | `PATCH /api/sensors/{id}/status` |

The browser performs basic positive-finite-cost, missing-endpoint and self-link
checks, but backend validation remains authoritative for identity, link
uniqueness, decommissioned sensors, and the **simulation-mode access policy**.
Production link writes are disabled by default unless explicitly opted in on
the server. This client is a teaching/demo interface, not a public
unauthenticated configuration console.

`useTopologySimulation.ts` holds pending state and operation feedback **above**
the conditional ready workspace. A confirmed HTTP mutation triggers a refetch
of topology, BFS, MST and selected route from the same graph revision.
Rejected or unconfirmed writes show an actionable error and are never
optimistically inserted into the visualization. The selection and overlay
mode persist across this refetch; existing SignalR invalidations and reconnect
refreshes continue to use the same source-of-truth loader.

The simulation changes **process-local logical topology and health**, not
physical IoT equipment or measured radio energy usage. A link's relative cost
is a configured positive finite simulation weight.

### Commit 3 verification

New Playwright suite:

`e2e/part3-topology-overlays.spec.ts`

It uses real backend sensor registration, status changes, topology snapshots and
link mutations. Assertions distinguish a **one-hop BFS route** from a
**two-hop Prim tree route** in the same graph; verify the exact edge IDs
highlighted; exercise link creation, cost update and deletion through the
interface; verify Offline/Online recovery without losing the selected sensor;
and reject a self-link without creating a fake edge.

The tests should be run locally using the existing managed API/Vite Playwright
configuration. No browser-run result is asserted by this document.

```bash
cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep 'P3-08 real API mesh routing overlays and simulation controls' --retries=0
CI=true npm run test:e2e -- --grep 'P3-08 accessible interactive mesh visualization' --retries=0
CI=true npm run test:e2e -- --grep 'Part 3 live topology API foundation' --retries=0
cd ../..
git diff --check
```

Commit 4 will add broader accessibility, mobile/failure/reconnection regression
and evidence capture. It will not replace the server's MST/BFS calculations.
