import type {
  ConsistentTopologyProjection,
  MeshDeviceRouteResponse,
  MeshLinkResponse,
} from '../../api/topologyApi';
import { sameMeshKey } from './meshLayout';

/**
 * The three explicit views required by P3-08. These values are UI-only:
 * the server remains responsible for BFS traversal and Prim's MST selection.
 */
export type MeshOverlayMode = 'connections' | 'bfs' | 'mst';

export interface MeshOverlayDescription {
  readonly mode: MeshOverlayMode;
  readonly highlightedLinkIds: ReadonlySet<string>;
  readonly selectedPathKeys: readonly string[];
  readonly explanation: string;
}

/**
 * Canonical unordered endpoint-pair lookup. Only links returned by the same
 * authoritative graph revision may be highlighted; no link is synthesized.
 */
function edgeKey(first: string, second: string): string {
  const a = first.toLowerCase();
  const b = second.toLowerCase();
  return a < b ? `${a}|${b}` : `${b}|${a}`;
}

function selectedRouteForRevision(
  projection: ConsistentTopologyProjection,
  selectedSensorId: string | null,
): MeshDeviceRouteResponse | null {
  const route = projection.selectedRoute;
  if (
    route === null ||
    selectedSensorId === null ||
    route.graphVersion !== projection.graphVersion ||
    !sameMeshKey(route.sensorProfileId, selectedSensorId)
  ) {
    return null;
  }
  return route;
}

/**
 * Produces presentational highlighting from immutable server results.
 * BFS uses the server-provided ordered path; MST uses the server-selected
 * link IDs. Neither branch executes a graph search in the browser.
 */
export function describeMeshOverlay(
  projection: ConsistentTopologyProjection,
  selectedSensorId: string | null,
  mode: MeshOverlayMode,
): MeshOverlayDescription {
  const links = projection.topology.links;

  if (mode === 'connections') {
    return {
      mode,
      highlightedLinkIds: new Set(links.map((link) => link.id)),
      selectedPathKeys: [],
      explanation: `All ${links.length} configured links are shown. Dashed connections touch an unavailable sensor and cannot currently forward traffic.`,
    };
  }

  if (mode === 'mst') {
    const configured = new Set(links.map((link) => link.id));
    const selected = new Set(
      projection.mst.selectedEdges.map((edge) => edge.linkId).filter((id) => configured.has(id)),
    );
    return {
      mode,
      highlightedLinkIds: selected,
      selectedPathKeys: [],
      explanation: `${selected.size} of ${links.length} configured links belong to the gateway-rooted Prim minimum-total-cost backbone. Total simulated backbone cost: ${projection.mst.totalSimulatedEnergyCost.toFixed(2)}. This minimizes total selected network cost, not every sensor's individual route cost.`,
    };
  }

  const route = selectedRouteForRevision(projection, selectedSensorId);
  if (route === null) {
    return {
      mode,
      highlightedLinkIds: new Set<string>(),
      selectedPathKeys: [],
      explanation:
        selectedSensorId === null
          ? 'Select a registered sensor to highlight its server-calculated BFS gateway path.'
          : 'No current BFS route is available for this selection. Choose a registered sensor or refresh the graph.',
    };
  }

  if (route.bfs.status !== 'Reachable') {
    return {
      mode,
      highlightedLinkIds: new Set<string>(),
      selectedPathKeys: [],
      explanation: `Selected sensor is ${route.bfs.status.toLowerCase()}; the API returned no eligible BFS route to the gateway.`,
    };
  }

  const byEndpoints = new Map<string, MeshLinkResponse>(
    links.map((link) => [edgeKey(link.nodeAKey, link.nodeBKey), link]),
  );
  const path = route.bfs.pathToGateway;
  const selected = new Set<string>();

  for (let index = 0; index < path.length - 1; index += 1) {
    const link = byEndpoints.get(edgeKey(path[index], path[index + 1]));
    if (link) {
      selected.add(link.id);
    }
  }

  return {
    mode,
    highlightedLinkIds: selected,
    selectedPathKeys: path,
    explanation: `BFS fewest-hop path: ${route.bfs.hopCount ?? 0} ${route.bfs.hopCount === 1 ? 'hop' : 'hops'} and ${selected.size} configured links, from the selected sensor to the gateway. Other connections are shown faintly for context.`,
  };
}
