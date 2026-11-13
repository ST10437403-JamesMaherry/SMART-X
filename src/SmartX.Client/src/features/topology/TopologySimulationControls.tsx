import { useState } from 'react';

import type { ConsistentTopologyProjection } from '../../api/topologyApi';
import type { SensorStatus } from '../../api/sensorsApi';
import { sameMeshKey } from './meshLayout';
import type { TopologySimulationActions } from './useTopologySimulation';

interface TopologySimulationControlsProps {
  readonly projection: ConsistentTopologyProjection;
  readonly selectedSensorId: string | null;
  readonly actions: TopologySimulationActions;
}

const DEMO_SENSOR_STATES: readonly SensorStatus[] = [
  'Online',
  'Warning',
  'Offline',
  'Fault',
  'Maintenance',
];

function nodeLabel(name: string | null, identifier: string | null, key: string): string {
  return name ? `${name} (${identifier ?? key})` : (identifier ?? key);
}

/**
 * All forms use the real P3-07 server endpoints. This UI never pretends that
 * an edge is persisted: it shows feedback only after an HTTP success and then
 * requests a new authoritative, revision-consistent topology projection.
 * Server-side simulation gating remains the security boundary.
 */
export function TopologySimulationControls({
  projection,
  selectedSensorId,
  actions,
}: TopologySimulationControlsProps) {
  const [firstKey, setFirstKey] = useState('gateway');
  const [secondKey, setSecondKey] = useState('');
  const [newCost, setNewCost] = useState('1.00');
  const [linkId, setLinkId] = useState('');
  const [linkCost, setLinkCost] = useState('1.00');
  const [desiredStatus, setDesiredStatus] = useState<SensorStatus>('Offline');

  const busy = actions.pending !== null;
  const eligibleVertices = projection.topology.nodes.filter(
    (node) => node.kind === 'Gateway' || node.status !== 'Decommissioned',
  );
  const selectedSensor = projection.topology.nodes.find(
    (node) =>
      node.kind === 'Sensor' &&
      selectedSensorId !== null &&
      node.sensorProfileId !== null &&
      sameMeshKey(node.sensorProfileId, selectedSensorId),
  );
  const selectedLink = projection.topology.links.find((link) => link.id === linkId);

  return (
    <section className="mesh-simulation" aria-labelledby="mesh-simulation-title">
      <div className="mesh-simulation__heading">
        <div>
          <p className="topology-workspace__eyebrow">NETWORK / SIMULATOR</p>
          <h3 id="mesh-simulation-title">Simulation controls</h3>
        </div>
        <span className="mesh-simulation__tag">Server-validated writes</span>
      </div>
      <p className="mesh-simulation__caution">
        Logical demonstration only — no physical radios are controlled. Link writes require an
        enabled development/test or explicitly configured simulation API. A successful request
        refreshes the live graph, BFS routes and Prim backbone.
      </p>

      <div className="mesh-simulation__grid">
        <form
          className="mesh-simulation__form"
          aria-label="Create simulated mesh link"
          onSubmit={(event) => {
            event.preventDefault();
            void actions.createLink(firstKey, secondKey, Number(newCost));
          }}
        >
          <h4>01 / Connect devices</h4>
          <label htmlFor="mesh-link-first">First endpoint</label>
          <select
            id="mesh-link-first"
            value={eligibleVertices.some((node) => node.key === firstKey) ? firstKey : ''}
            disabled={busy}
            onChange={(event) => setFirstKey(event.target.value)}
            required
          >
            <option value="">Select first endpoint</option>
            {eligibleVertices.map((node) => (
              <option key={node.key} value={node.key}>
                {node.kind === 'Gateway'
                  ? 'Logical gateway'
                  : nodeLabel(node.name, node.deviceIdentifier, node.key)}
              </option>
            ))}
          </select>

          <label htmlFor="mesh-link-second">Second endpoint</label>
          <select
            id="mesh-link-second"
            value={eligibleVertices.some((node) => node.key === secondKey) ? secondKey : ''}
            disabled={busy}
            onChange={(event) => setSecondKey(event.target.value)}
            required
          >
            <option value="">Select second endpoint</option>
            {eligibleVertices.map((node) => (
              <option key={node.key} value={node.key}>
                {node.kind === 'Gateway'
                  ? 'Logical gateway'
                  : nodeLabel(node.name, node.deviceIdentifier, node.key)}
              </option>
            ))}
          </select>

          <label htmlFor="mesh-link-create-cost">Relative energy cost</label>
          <input
            id="mesh-link-create-cost"
            type="number"
            min="0.000001"
            step="any"
            value={newCost}
            onChange={(event) => setNewCost(event.target.value)}
            disabled={busy}
            required
          />
          <button disabled={busy || eligibleVertices.length < 2} type="submit">
            {actions.pending === 'create' ? 'Creating link…' : 'Create simulated link'}
          </button>
        </form>

        <form
          className="mesh-simulation__form"
          aria-label="Edit simulated mesh link"
          onSubmit={(event) => {
            event.preventDefault();
            void actions.changeCost(linkId, Number(linkCost));
          }}
        >
          <h4>02 / Manage links</h4>
          <label htmlFor="mesh-existing-link">Configured connection</label>
          <select
            id="mesh-existing-link"
            value={selectedLink ? linkId : ''}
            disabled={busy || projection.topology.links.length === 0}
            onChange={(event) => {
              const next = projection.topology.links.find((link) => link.id === event.target.value);
              setLinkId(event.target.value);
              if (next) {
                setLinkCost(String(next.simulatedEnergyCost));
              }
            }}
            required
          >
            <option value="">Select configured link</option>
            {projection.topology.links.map((link) => (
              <option key={link.id} value={link.id}>
                {link.nodeAKey} ↔ {link.nodeBKey} ({link.simulatedEnergyCost.toFixed(2)})
              </option>
            ))}
          </select>

          <label htmlFor="mesh-link-updated-cost">New relative cost</label>
          <input
            id="mesh-link-updated-cost"
            type="number"
            min="0.000001"
            step="any"
            value={linkCost}
            onChange={(event) => setLinkCost(event.target.value)}
            disabled={busy || !selectedLink}
            required
          />
          <div className="mesh-simulation__actions">
            <button disabled={busy || !selectedLink} type="submit">
              {actions.pending === 'cost' ? 'Updating…' : 'Update link cost'}
            </button>
            <button
              className="mesh-simulation__remove"
              disabled={busy || !selectedLink}
              type="button"
              onClick={() => {
                if (selectedLink && window.confirm('Remove this simulated mesh connection?')) {
                  void actions.deleteLink(selectedLink.id);
                }
              }}
            >
              {actions.pending === 'remove' ? 'Removing…' : 'Remove link'}
            </button>
          </div>
          <p className="mesh-simulation__hint">Link deletion requires confirmation.</p>
        </form>

        <form
          className="mesh-simulation__form"
          aria-label="Change simulated sensor health"
          onSubmit={(event) => {
            event.preventDefault();
            if (selectedSensor?.sensorProfileId) {
              void actions.changeHealth(selectedSensor.sensorProfileId, desiredStatus);
            }
          }}
        >
          <h4>03 / Simulate relay health</h4>
          <p className="mesh-simulation__selection">
            Selected: <strong>{selectedSensor?.name ?? 'No sensor selected'}</strong>
          </p>
          <p className="mesh-simulation__hint">
            Current status: {selectedSensor?.status ?? 'Not available'}. Offline, Fault and
            Maintenance sensors cannot forward traffic.
          </p>
          <label htmlFor="mesh-health-status">New simulated health</label>
          <select
            id="mesh-health-status"
            value={desiredStatus}
            onChange={(event) => setDesiredStatus(event.target.value as SensorStatus)}
            disabled={busy || !selectedSensor || selectedSensor.status === 'Decommissioned'}
          >
            {DEMO_SENSOR_STATES.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
          <button
            type="submit"
            disabled={busy || !selectedSensor || selectedSensor.status === 'Decommissioned'}
          >
            {actions.pending === 'health' ? 'Applying status…' : 'Apply simulated status'}
          </button>
        </form>
      </div>
    </section>
  );
}
