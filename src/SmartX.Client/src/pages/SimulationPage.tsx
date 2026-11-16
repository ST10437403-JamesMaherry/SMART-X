import { useCallback, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import ApplicationShell from '../components/ApplicationShell';
import { getApiErrorMessage } from '../api/apiTypes';
import { loadConsistentTopology, type ConsistentTopologyProjection } from '../api/topologyApi';
import { MeshCanvas } from '../features/topology/MeshCanvas';
import TelemetryTrace from '../features/simulation/TelemetryTrace';
import { buildMeshDiagram, sortMeshSensors } from '../features/topology/meshLayout';
import { describeMeshOverlay, type MeshOverlayMode } from '../features/topology/meshOverlay';
import { useTopologyData } from '../features/topology/useTopologyData';
import {
  applyScenario,
  matchScenario,
  ScenarioSafetyError,
  scenarios,
  type ScenarioDefinition,
  type ScenarioId,
} from '../features/simulation/scenarios';
import { routes } from '../routing/routes';
import '../features/topology/TopologyWorkspace.css';
import './SimulationPage.css';

interface Summary {
  readonly reachable: number;
  readonly disconnected: number;
  readonly unavailable: number;
  readonly cost: number;
  readonly revision: number;
}

function summarize(projection: ConsistentTopologyProjection): Summary {
  return {
    reachable: projection.reachability.reachableSensorCount,
    disconnected: projection.reachability.disconnectedSensorCount,
    unavailable: projection.reachability.unavailableSensorCount,
    cost: projection.mst.totalSimulatedEnergyCost,
    revision: projection.graphVersion,
  };
}

function signed(value: number, decimals = 0): string {
  if (Math.abs(value) < 0.000001) return 'no change';
  const formatted = value.toFixed(decimals);
  return value > 0 ? `+${formatted}` : formatted;
}

function resultExplanation(scenario: ScenarioDefinition | null): string {
  switch (scenario?.id) {
    case 'healthy':
    case 'recovered':
      return 'All six devices can reach the gateway. Prim avoids the expensive direct backup link because a lower-total-cost backbone exists.';
    case 'relay-failure':
      return 'The primary relay is unavailable. A backup gateway link saves four sensors, but the single-bridge moisture probe loses its only path.';
    case 'island':
      return 'Both gateway entry points are unusable. Five healthy sensors remain in disconnected islands; Prim selects no gateway-rooted edges.';
    default:
      return 'These are actual API results. This graph does not currently match a complete greenhouse demonstration scenario.';
  }
}

/**
 * A guided operator workflow layered over the existing HTTP graph and actions.
 * Never computes reachability or a spanning tree in the browser.
 */
export default function SimulationPage() {
  const {
    phase,
    projection,
    errorMessage,
    selectedSensorId,
    setSelectedSensorId,
    refreshTopology,
    realtimeState,
  } = useTopologyData();
  const [overlayMode, setOverlayMode] = useState<MeshOverlayMode>('connections');
  const [traceBusy, setTraceBusy] = useState(false);
  const [pending, setPending] = useState<ScenarioId | null>(null);
  const [feedback, setFeedback] = useState<{ kind: 'success' | 'error'; message: string } | null>(
    null,
  );
  const [comparison, setComparison] = useState<{ before: Summary; after: Summary } | null>(null);
  const busy = useRef(false);

  const runScenario = useCallback(
    async (id: ScenarioId) => {
      if (busy.current || traceBusy) return;
      busy.current = true;
      setPending(id);
      setFeedback(null);
      setComparison(null);

      try {
        const before = await loadConsistentTopology(null);
        await applyScenario(id);
        // Confirm using the API rather than trusting the action's intended result.
        const after = await loadConsistentTopology(null);
        const expected = scenarios.find((item) => item.id === id);
        if (!expected) throw new Error('Unknown simulation scenario.');
        if (
          after.reachability.registeredSensorCount !== 6 ||
          after.reachability.reachableSensorCount !== expected.reachable ||
          after.reachability.disconnectedSensorCount !== expected.disconnected ||
          after.reachability.unavailableSensorCount !== expected.unavailable ||
          after.topology.linkCount !== expected.links ||
          Math.abs(after.mst.totalSimulatedEnergyCost - expected.mstCost) > 0.000001
        ) {
          throw new Error(
            'The server results differ from the expected scenario. Inspect the current graph before retrying.',
          );
        }

        setComparison({ before: summarize(before), after: summarize(after) });
        setFeedback({
          kind: 'success',
          message: `${expected.title} applied and verified against the current server results.`,
        });
        // An intentionally visible BFS/MST comparison is more useful than
        // automatically scrolling the user away from the scenario controls.
        setOverlayMode('mst');
        refreshTopology();
      } catch (error: unknown) {
        setFeedback({
          kind: 'error',
          message: `${error instanceof ScenarioSafetyError ? error.message : getApiErrorMessage(error, error instanceof Error ? error.message : 'The scenario could not be completed.')} Some writes may already have happened. Refresh the network before retrying; there is no automatic rollback.`,
        });
        refreshTopology();
      } finally {
        busy.current = false;
        setPending(null);
      }
    },
    [refreshTopology, traceBusy],
  );

  const active = projection === null ? null : matchScenario(projection);
  const sensors = projection
    ? sortMeshSensors(projection.topology.nodes.filter((node) => node.kind === 'Sensor'))
    : [];
  const diagram = projection ? buildMeshDiagram(projection, sensors, selectedSensorId) : null;
  const overlay = projection
    ? describeMeshOverlay(projection, selectedSensorId, overlayMode)
    : null;

  // A selected route is fetched by the authoritative topology hook.
  return (
    <ApplicationShell>
      <section className="simulation-intro" aria-labelledby="page-title">
        <p className="eyebrow">SMART-X / GUIDED EXPERIENCE</p>
        <h1 className="page-title" id="page-title">
          Simulation Lab
        </h1>
        <p className="page-description">
          Understand the entire system in one place. Load a network, break a relay, inspect the
          algorithm decisions and restore connectivity. All results come from the running local
          Smart-X API.
        </p>
        <div className="simulation-steps" aria-label="How the simulation works">
          <span>01 · Choose a scenario</span>
          <span>02 · Inspect the network</span>
          <span>03 · Compare the result</span>
          <span>04 · Trace telemetry</span>
          <span>05 · Explore deeper</span>
        </div>
      </section>

      <section className="simulation-section" aria-labelledby="scenario-heading">
        <div className="simulation-section__heading">
          <div>
            <p className="simulation-kicker">STEP 01 / EXPERIMENT</p>
            <h2 id="scenario-heading">Choose what happens</h2>
            <p>
              Each button operates the same six simulated greenhouse devices through the normal
              sensor and topology APIs.
            </p>
          </div>
          <span className="simulation-section__tag">LOCAL ONLY</span>
        </div>

        <div className="scenario-grid">
          {scenarios.map((scenario) => (
            <div className="scenario-card" key={scenario.id}>
              <h3>{scenario.title}</h3>
              <p>{scenario.description}</p>
              <button
                type="button"
                disabled={pending !== null || traceBusy || phase !== 'ready'}
                onClick={() => void runScenario(scenario.id)}
                aria-label={`Run ${scenario.title}`}
              >
                {pending === scenario.id ? 'Running…' : 'Run scenario'}
              </button>
            </div>
          ))}
        </div>
        <p className="simulation-help">
          On first run, Smart-X registers six demo sensors. Subsequent runs reuse them. The lab
          refuses unrelated devices or links instead of resetting your work. Changes affect
          simulated, process-local state only.
        </p>
        {feedback && (
          <p
            className={`simulation-feedback simulation-feedback--${feedback.kind}`}
            data-testid="simulation-feedback"
            role={feedback.kind === 'error' ? 'alert' : 'status'}
          >
            {feedback.message}
          </p>
        )}
      </section>

      <section className="simulation-section" aria-labelledby="network-heading">
        <div className="simulation-section__heading">
          <div>
            <p className="simulation-kicker">STEP 02 / AUTHORITATIVE GRAPH</p>
            <h2 id="network-heading">What's happening in the network?</h2>
            <p>
              Explore the actual network revision, not a static diagram or a client-side simulation.
            </p>
          </div>
          <button
            className="simulation-secondary"
            type="button"
            disabled={pending !== null}
            onClick={refreshTopology}
          >
            Refresh data
          </button>
        </div>

        {phase === 'loading' && (
          <p role="status" className="simulation-help">
            Loading the latest consistent graph revision…
          </p>
        )}
        {phase === 'error' && (
          <p role="alert" className="simulation-feedback simulation-feedback--error">
            {errorMessage ?? 'Unable to load topology. Check whether Docker is running.'}
          </p>
        )}
        {phase === 'ready' && projection && diagram && overlay && (
          <>
            <div className="simulation-meta">
              <span>GRAPH REV {projection.graphVersion}</span>
              <span>REALTIME {realtimeState.toUpperCase()}</span>
              <span>
                {active ? `KNOWN FIXTURE / ${active.title}` : 'CUSTOM OR EMPTY WORKSPACE'}
              </span>
            </div>
            <dl className="simulation-metrics" aria-label="Current network metrics">
              <div>
                <dt>Registered</dt>
                <dd>{projection.reachability.registeredSensorCount}</dd>
              </div>
              <div>
                <dt>Reachable</dt>
                <dd>{projection.reachability.reachableSensorCount}</dd>
              </div>
              <div>
                <dt>Disconnected</dt>
                <dd>{projection.reachability.disconnectedSensorCount}</dd>
              </div>
              <div>
                <dt>Unavailable</dt>
                <dd>{projection.reachability.unavailableSensorCount}</dd>
              </div>
              <div>
                <dt>Mesh links</dt>
                <dd>{projection.topology.linkCount}</dd>
              </div>
              <div>
                <dt>Prim backbone cost</dt>
                <dd>{projection.mst.totalSimulatedEnergyCost.toFixed(2)}</dd>
              </div>
            </dl>
            <p className="simulation-interpretation" data-testid="simulation-interpretation">
              {resultExplanation(active)}
            </p>

            <div className="simulation-view-switch" role="group" aria-label="Graph overlay mode">
              <button
                type="button"
                aria-pressed={overlayMode === 'connections'}
                onClick={() => setOverlayMode('connections')}
              >
                Configured links
              </button>
              <button
                type="button"
                aria-pressed={overlayMode === 'bfs'}
                onClick={() => setOverlayMode('bfs')}
              >
                BFS route
              </button>
              <button
                type="button"
                aria-pressed={overlayMode === 'mst'}
                onClick={() => setOverlayMode('mst')}
              >
                Prim backbone
              </button>
            </div>

            {sensors.length > 0 && (
              <div className="simulation-selector">
                <label htmlFor="simulation-selected-sensor">Inspect a sensor's gateway route</label>
                <select
                  id="simulation-selected-sensor"
                  value={selectedSensorId ?? ''}
                  onChange={(event) => setSelectedSensorId(event.target.value || null)}
                >
                  <option value="">Choose a sensor</option>
                  {sensors.map((sensor) => (
                    <option key={sensor.key} value={sensor.sensorProfileId ?? ''}>
                      {sensor.name} — {sensor.status}
                    </option>
                  ))}
                </select>
              </div>
            )}
            <MeshCanvas
              layout={diagram}
              overlay={overlay}
              selectedSensorId={selectedSensorId}
              onSelectSensor={setSelectedSensorId}
            />
            {projection.selectedRoute && (
              <div className="simulation-route" role="status">
                <strong>{projection.selectedRoute.status}</strong>
                <span>BFS: {projection.selectedRoute.bfs.hopCount ?? '—'} hops</span>
                <span>
                  Prim tree route:{' '}
                  {projection.selectedRoute.mst.accumulatedSimulatedEnergyCost?.toFixed(2) ?? '—'}{' '}
                  relative cost
                </span>
              </div>
            )}
            <div className="simulation-explanation-grid">
              <article>
                <h3>BFS / Fewest hops</h3>
                <p>
                  Finds the shortest route by number of links from one selected device to the
                  gateway. It does not minimize link weights.
                </p>
              </article>
              <article>
                <h3>Prim / Minimum spanning tree</h3>
                <p>
                  Selects the gateway-connected set of links with minimum total simulated cost. The
                  tree is not necessarily the shortest route for an individual sensor.
                </p>
              </article>
            </div>
          </>
        )}
      </section>

      <section className="simulation-section" aria-labelledby="results-heading">
        <div className="simulation-section__heading">
          <div>
            <p className="simulation-kicker">STEP 03 / COMPARE</p>
            <h2 id="results-heading">What changed?</h2>
          </div>
        </div>
        {comparison ? (
          <div className="simulation-comparison" data-testid="simulation-comparison">
            <table>
              <caption>Network metrics before and after the most recent verified scenario</caption>
              <thead>
                <tr>
                  <th scope="col">Metric</th>
                  <th scope="col">Before</th>
                  <th scope="col">After</th>
                  <th scope="col">Change</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <th scope="row">Reachable sensors</th>
                  <td>{comparison.before.reachable}</td>
                  <td>{comparison.after.reachable}</td>
                  <td>{signed(comparison.after.reachable - comparison.before.reachable)}</td>
                </tr>
                <tr>
                  <th scope="row">Disconnected sensors</th>
                  <td>{comparison.before.disconnected}</td>
                  <td>{comparison.after.disconnected}</td>
                  <td>{signed(comparison.after.disconnected - comparison.before.disconnected)}</td>
                </tr>
                <tr>
                  <th scope="row">Unavailable sensors</th>
                  <td>{comparison.before.unavailable}</td>
                  <td>{comparison.after.unavailable}</td>
                  <td>{signed(comparison.after.unavailable - comparison.before.unavailable)}</td>
                </tr>
                <tr>
                  <th scope="row">Prim backbone cost</th>
                  <td>{comparison.before.cost.toFixed(2)}</td>
                  <td>{comparison.after.cost.toFixed(2)}</td>
                  <td>{signed(comparison.after.cost - comparison.before.cost, 2)}</td>
                </tr>
              </tbody>
            </table>
            <p>
              Compared graph revisions {comparison.before.revision} → {comparison.after.revision}.
              These are observed API values, not predicted results.
            </p>
          </div>
        ) : (
          <p className="simulation-help">
            Run a scenario to capture a verified before-and-after comparison.
          </p>
        )}
      </section>

      <TelemetryTrace
        enabled={phase === 'ready' && (active?.id === 'healthy' || active?.id === 'recovered')}
        scenarioBusy={pending !== null || phase !== 'ready'}
        onBusyChange={setTraceBusy}
      />

      <section className="simulation-section simulation-next" aria-labelledby="next-heading">
        <div className="simulation-section__heading">
          <div>
            <p className="simulation-kicker">STEP 05 / DEEPER WORKBENCHES</p>
            <h2 id="next-heading">Follow the data through Smart-X</h2>
            <p>
              The Simulation Lab is a guided starting point. Each workbench exposes a different
              layer of the same application.
            </p>
          </div>
        </div>
        <div className="simulation-next__grid">
          <Link to={routes.ingestion}>
            <span>01 / INPUT</span>
            <strong>Sensor &amp; telemetry</strong>
            <small>Register sensors, send readings, inspect ingestion and anomalies.</small>
          </Link>
          <Link to={routes.realtime}>
            <span>02 / PROCESS</span>
            <strong>Real-time operations</strong>
            <small>Follow queues, worker output, events and commands.</small>
          </Link>
          <Link to={routes.topology}>
            <span>03 / ANALYZE</span>
            <strong>Network topology</strong>
            <small>Inspect route overlays, mesh links, history and insights.</small>
          </Link>
        </div>
      </section>
      <p className="simulation-disclaimer">
        Smart-X is a local software simulation, not a real radio controller. No cloud APIs or
        external service keys are required. Do not expose this unauthenticated control plane to the
        public internet.
      </p>
    </ApplicationShell>
  );
}
