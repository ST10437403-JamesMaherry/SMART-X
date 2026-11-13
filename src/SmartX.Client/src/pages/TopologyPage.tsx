import { useState } from 'react';

import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';
import { TopologyWorkspace, type MeshViewMode } from '../features/topology/TopologyWorkspace';
import { TopologySensorHistory } from '../features/topology/TopologySensorHistory';
import { TopologyInsights } from '../features/topology/TopologyInsights';
import { useTopologyData } from '../features/topology/useTopologyData';
import type { MeshOverlayMode } from '../features/topology/meshOverlay';
import { useTopologySimulation } from '../features/topology/useTopologySimulation';
import '../features/topology/TopologyDataStatus.css';

/**
 * P3-08 Commit 3 retains the proven P3-08 Commit 1 load/error/selection
 * contract and mounts the accessible graph and table only after all related
 * topology projections agree on one authoritative graph revision.
 */
function TopologyPage() {
  // Keep visual controls mounted while useTopologyData refetches on selection.
  // These are local presentation preferences, not a second sensor store.
  const [meshSearch, setMeshSearch] = useState('');
  const [meshView, setMeshView] = useState<MeshViewMode>('diagram');
  const [meshOverlayMode, setMeshOverlayMode] = useState<MeshOverlayMode>('connections');
  const [meshPage, setMeshPage] = useState(0);
  const {
    phase,
    projection,
    errorMessage,
    selectedSensorId,
    setSelectedSensorId,
    realtimeState,
    refreshTopology,
  } = useTopologyData();
  const simulation = useTopologySimulation(refreshTopology);

  const sensors = projection?.topology.nodes.filter((node) => node.kind === 'Sensor') ?? [];
  const selected = selectedSensorId
    ? sensors.find((node) => node.sensorProfileId?.toLowerCase() === selectedSensorId.toLowerCase())
    : undefined;
  const selectedRoute = projection?.selectedRoute;
  const selectionIsMissing = selectedSensorId !== null && selected === undefined;

  return (
    <ApplicationShell>
      <section className="page-intro module-heading" aria-labelledby="page-title">
        <p className="eyebrow">PART 3 / LIVE TOPOLOGY</p>
        <h1 className="page-title" id="page-title">
          Network Topology
          <br />
          &amp; Mesh Routing
        </h1>
        <p className="page-description">
          Explore registered sensors, gateway reachability and minimum-cost mesh backbones. All
          links and routing results come from Smart-X; positions are not physical locations.
        </p>
      </section>

      <PillarNavigation />

      <section className="topology-bootstrap" aria-labelledby="topology-data-heading">
        <h2 className="topology-bootstrap__heading" id="topology-data-heading">
          Live mesh topology
        </h2>

        {simulation.feedback !== null && (
          <p
            className={`topology-bootstrap__mutation topology-bootstrap__mutation--${simulation.feedback.kind}`}
            role={simulation.feedback.kind === 'error' ? 'alert' : 'status'}
            data-testid="topology-simulation-feedback"
          >
            {simulation.feedback.message}
          </p>
        )}

        <div aria-live="polite" aria-atomic="true">
          {phase === 'loading' && (
            <p className="topology-bootstrap__meta" role="status">
              Loading the authoritative topology and checking graph revisions…
            </p>
          )}

          {phase === 'error' && (
            <div role="alert">
              <p className="topology-bootstrap__meta">The current topology could not be loaded.</p>
              <p className="topology-bootstrap__note">{errorMessage}</p>
              <button className="text-button" type="button" onClick={refreshTopology}>
                Retry topology request
              </button>
            </div>
          )}
        </div>

        {phase === 'ready' && projection !== null && (
          <>
            <p className="topology-bootstrap__meta">
              Graph revision {projection.graphVersion} · Realtime channel: {realtimeState}
            </p>

            <dl className="topology-bootstrap__stats" aria-label="Authoritative mesh summary">
              <div className="topology-bootstrap__stat">
                <dt>Registered sensors</dt>
                <dd>{projection.reachability.registeredSensorCount}</dd>
              </div>
              <div className="topology-bootstrap__stat">
                <dt>Configured links</dt>
                <dd>{projection.topology.linkCount}</dd>
              </div>
              <div className="topology-bootstrap__stat">
                <dt>Gateway reachable</dt>
                <dd>{projection.reachability.reachableSensorCount}</dd>
              </div>
              <div className="topology-bootstrap__stat">
                <dt>Disconnected</dt>
                <dd>{projection.reachability.disconnectedSensorCount}</dd>
              </div>
              <div className="topology-bootstrap__stat">
                <dt>Unavailable</dt>
                <dd>{projection.reachability.unavailableSensorCount}</dd>
              </div>
              <div className="topology-bootstrap__stat">
                <dt>MST backbone cost</dt>
                <dd>{projection.mst.totalSimulatedEnergyCost.toFixed(2)}</dd>
              </div>
            </dl>

            {sensors.length === 0 && (
              <p className="topology-bootstrap__note">
                No registered devices yet. Register sensors in the ingestion pillar, then create
                explicit simulated links; the dashboard does not invent connections.
              </p>
            )}

            <div className="topology-bootstrap__controls">
              <div className="topology-bootstrap__field">
                <label htmlFor="topology-selected-sensor">Select a registered sensor</label>
                <select
                  id="topology-selected-sensor"
                  value={selectedSensorId ?? ''}
                  onChange={(event) => setSelectedSensorId(event.target.value || null)}
                >
                  <option value="">No sensor selected</option>
                  {selectionIsMissing && (
                    <option value={selectedSensorId ?? ''}>
                      Previously selected sensor is not in this mesh
                    </option>
                  )}
                  {sensors.map((node) => (
                    <option key={node.key} value={node.sensorProfileId ?? ''}>
                      {node.name} — {node.deviceIdentifier}
                    </option>
                  ))}
                </select>
              </div>
              <button className="text-button" type="button" onClick={refreshTopology}>
                Refresh topology
              </button>
            </div>

            {selectionIsMissing && (
              <p className="topology-bootstrap__selected">
                Your selected sensor is no longer part of the current registered mesh. The
                cross-pillar selection has been preserved.
              </p>
            )}

            {selected !== undefined && selectedRoute !== undefined && selectedRoute !== null && (
              <p className="topology-bootstrap__selected" role="status">
                {selected.name}: {selected.status} · Gateway route: {selectedRoute.status} · BFS
                hops: {selectedRoute.bfs.hopCount ?? 'not available'} · MST tree route cost:{' '}
                {selectedRoute.mst.accumulatedSimulatedEnergyCost?.toFixed(2) ?? 'not available'}
              </p>
            )}

            {/* Rule-derived facts use this exact versioned graph, never a
                browser-synthesised route or historical training record. */}
            <TopologyInsights
              surface="topology"
              refreshToken={projection.graphVersion}
              expectedGraphVersion={projection.graphVersion}
              nodes={projection.topology.nodes}
            />

            <TopologyWorkspace
              projection={projection}
              selectedSensorId={selectedSensorId}
              onSelectSensor={setSelectedSensorId}
              search={meshSearch}
              onSearchChange={(value) => {
                setMeshSearch(value);
                setMeshPage(0);
              }}
              view={meshView}
              onViewChange={setMeshView}
              overlayMode={meshOverlayMode}
              onOverlayModeChange={setMeshOverlayMode}
              simulation={simulation}
              requestedPage={meshPage}
              onPageChange={setMeshPage}
            />
          </>
        )}
      </section>

      {/*
       * The history view is mounted outside the asynchronous graph-ready
       * branch. Refreshing BFS/MST therefore cannot erase the operator's
       * local range inputs or an in-flight AVL query. Changing sensors gets
       * a fresh component instance and aborts the old device request.
       */}
      <TopologySensorHistory
        key={selectedSensorId ?? 'no-sensor'}
        sensorProfileId={selectedSensorId}
        sensorName={selected?.name ?? null}
        deviceIdentifier={selected?.deviceIdentifier ?? null}
        telemetryDataType={selected?.telemetryDataType ?? null}
      />
    </ApplicationShell>
  );
}

export default TopologyPage;
