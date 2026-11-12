import type {
  ConsistentTopologyProjection,
  MeshDeviceRouteResponse,
  MeshVertexResponse,
} from '../../api/topologyApi';
import { SelectedSensorLinks } from '../../components/SelectedSensorLinks';
import { getMeshConnection, sameMeshKey } from './meshLayout';

interface TopologyNodeDetailsProps {
  readonly projection: ConsistentTopologyProjection;
  readonly selectedSensorId: string | null;
}

/**
 * A fully textual explanation of the server-selected sensor. The panel never
 * fabricates missing routes and clearly separates BFS hops, MST tree costs,
 * and the overall backbone's total cost.
 */
export function TopologyNodeDetails({ projection, selectedSensorId }: TopologyNodeDetailsProps) {
  const sensor = projection.topology.nodes.find(
    (node) =>
      node.kind === 'Sensor' &&
      node.sensorProfileId !== null &&
      selectedSensorId !== null &&
      sameMeshKey(node.sensorProfileId, selectedSensorId),
  );

  if (selectedSensorId === null) {
    return (
      <aside className="mesh-details" aria-label="Selected device details">
        <p className="mesh-details__eyebrow">INSPECT / DEVICE</p>
        <h3 className="mesh-details__heading">No sensor selected</h3>
        <p className="mesh-details__hint">
          Select a numbered node in the diagram, a row in the table, or a registered sensor above to
          inspect its real identity and route.
        </p>
      </aside>
    );
  }

  if (!sensor) {
    return (
      <aside className="mesh-details" aria-label="Selected device details">
        <p className="mesh-details__eyebrow">INSPECT / DEVICE</p>
        <h3 className="mesh-details__heading">Sensor no longer registered</h3>
        <p className="mesh-details__hint">
          Your cross-pillar selection is preserved, but this sensor does not appear in the current
          authoritative graph. Choose another registered device to inspect it.
        </p>
      </aside>
    );
  }

  const route = projection.selectedRoute;
  const routeMatches =
    route !== null &&
    route.graphVersion === projection.graphVersion &&
    sameMeshKey(route.sensorProfileId, sensor.sensorProfileId ?? '');
  const selectedRoute: MeshDeviceRouteResponse | null = routeMatches ? route : null;
  const connection = getMeshConnection(sensor, projection);
  const neighbors = projection.topology.links
    .filter(
      (link) => sameMeshKey(link.nodeAKey, sensor.key) || sameMeshKey(link.nodeBKey, sensor.key),
    )
    .map((link) => {
      const neighborKey = sameMeshKey(link.nodeAKey, sensor.key) ? link.nodeBKey : link.nodeAKey;
      return {
        link,
        other: projection.topology.nodes.find((node) => sameMeshKey(node.key, neighborKey)),
        neighborKey,
      };
    })
    .sort((first, second) =>
      first.link.id < second.link.id ? -1 : first.link.id > second.link.id ? 1 : 0,
    );
  const isInTree = projection.mst.reachedNodeKeys.some((key) => sameMeshKey(key, sensor.key));

  function stepLabel(key: string): string {
    const node: MeshVertexResponse | undefined = projection.topology.nodes.find((candidate) =>
      sameMeshKey(candidate.key, key),
    );
    return node?.kind === 'Gateway' ? 'Gateway' : (node?.name ?? node?.deviceIdentifier ?? key);
  }

  return (
    <aside className="mesh-details" aria-label="Selected device details">
      <p className="mesh-details__eyebrow">INSPECT / REGISTERED SENSOR</p>
      <h3 className="mesh-details__heading">
        {sensor.name ?? sensor.deviceIdentifier ?? sensor.key}
      </h3>
      <p className="mesh-details__state" role="status">
        <span className={`mesh-status mesh-status--${connection.toLowerCase()}`}>{connection}</span>
        <span>{sensor.status ?? 'Unknown health'}</span>
      </p>

      <dl className="mesh-details__facts">
        <div>
          <dt>Device identifier</dt>
          <dd>{sensor.deviceIdentifier ?? 'Not available'}</dd>
        </div>
        <div>
          <dt>Sensor ID</dt>
          <dd>{sensor.sensorProfileId}</dd>
        </div>
        <div>
          <dt>Category / data type</dt>
          <dd>
            {sensor.category ?? 'Unknown'} / {sensor.telemetryDataType ?? 'Unknown'}
          </dd>
        </div>
        <div>
          <dt>Deployment zone</dt>
          <dd>
            {sensor.deploymentLocation
              ? `${sensor.deploymentLocation.facility} / ${sensor.deploymentLocation.zone}`
              : 'Not available'}
          </dd>
        </div>
        <div>
          <dt>MST membership</dt>
          <dd>{isInTree ? 'Included in gateway backbone' : 'Outside reachable backbone'}</dd>
        </div>
        <div>
          <dt>BFS minimum hops</dt>
          <dd>{selectedRoute?.bfs.hopCount ?? 'No gateway route'}</dd>
        </div>
        <div>
          <dt>MST selected-tree route cost</dt>
          <dd>
            {selectedRoute?.mst.accumulatedSimulatedEnergyCost == null
              ? 'No gateway route'
              : selectedRoute.mst.accumulatedSimulatedEnergyCost.toFixed(2)}
          </dd>
        </div>
      </dl>

      <div className="mesh-details__routes">
        <h4>BFS / Fewest hops</h4>
        {selectedRoute?.bfs.pathToGateway.length ? (
          <ol aria-label="BFS route from selected sensor to gateway">
            {selectedRoute.bfs.pathToGateway.map((key) => (
              <li key={key}>{stepLabel(key)}</li>
            ))}
          </ol>
        ) : (
          <p>No eligible gateway path for this sensor.</p>
        )}
        <h4>Prim / Selected backbone route</h4>
        {selectedRoute?.mst.pathToGateway.length ? (
          <ol aria-label="MST tree route from selected sensor to gateway">
            {selectedRoute.mst.pathToGateway.map((key) => (
              <li key={key}>{stepLabel(key)}</li>
            ))}
          </ol>
        ) : (
          <p>No selected-tree route for this sensor.</p>
        )}
      </div>

      <div className="mesh-details__links">
        <h4>Configured neighboring links ({neighbors.length})</h4>
        {neighbors.length === 0 ? (
          <p>This device has no configured mesh links.</p>
        ) : (
          <ul>
            {neighbors.map(({ link, other, neighborKey }) => (
              <li key={link.id}>
                <span>{other?.kind === 'Gateway' ? 'Gateway' : (other?.name ?? neighborKey)}</span>
                <span>{link.simulatedEnergyCost.toFixed(2)} cost</span>
              </li>
            ))}
          </ul>
        )}
      </div>
      <SelectedSensorLinks
        source="topology"
        sensorName={sensor.name ?? sensor.deviceIdentifier ?? sensor.key}
      />
      <p className="mesh-details__disclaimer">
        Costs are simulated relative weights, not measured energy. A Prim backbone minimizes total
        selected network cost, not every individual weighted route.
      </p>
    </aside>
  );
}
