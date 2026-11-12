import { ApiError } from './apiTypes';
import { getJson } from './apiClient';
import type {
  IdentifierKind,
  SensorCategory,
  SensorDeploymentLocation,
  SensorStatus,
  TelemetryDataType,
} from './sensorsApi';

/**
 * P3-07 transport contracts, mapped one-for-one from SmartX.Contracts.Topology.
 * GUIDs and canonical node keys remain strings because they are identities,
 * not values that the frontend can safely derive or reinterpret.
 */
export type MeshNodeKind = 'Gateway' | 'Sensor';
export type MeshRouteStatus = 'Reachable' | 'Disconnected' | 'Unavailable';

export interface MeshVertexResponse {
  readonly key: string;
  readonly kind: MeshNodeKind;
  readonly sensorProfileId: string | null;
  readonly name: string | null;
  readonly deviceIdentifier: string | null;
  readonly identifierKind: IdentifierKind | null;
  readonly category: SensorCategory | null;
  readonly status: SensorStatus | null;
  readonly isForwardingEligible: boolean;
  readonly telemetryDataType: TelemetryDataType | null;
  readonly deploymentLocation: SensorDeploymentLocation | null;
}

export interface MeshLinkResponse {
  readonly id: string;
  readonly nodeAKey: string;
  readonly nodeBKey: string;
  /** Positive relative simulation weight, NOT measured energy consumption. */
  readonly simulatedEnergyCost: number;
}

export interface MeshTopologyResponse {
  readonly graphVersion: number;
  readonly gatewayKey: string;
  readonly nodeCount: number;
  readonly linkCount: number;
  readonly nodes: readonly MeshVertexResponse[];
  readonly links: readonly MeshLinkResponse[];
}

export interface MeshHopCountResponse {
  readonly nodeKey: string;
  readonly hopCount: number;
}

export interface MeshDisconnectedComponentResponse {
  readonly sensorKeys: readonly string[];
}

export interface MeshReachabilityResponse {
  readonly graphVersion: number;
  readonly gatewayKey: string;
  readonly registeredSensorCount: number;
  readonly reachableSensorCount: number;
  readonly disconnectedSensorCount: number;
  readonly unavailableSensorCount: number;
  /** Includes the gateway itself, with a hop count of zero. */
  readonly reachableNodes: readonly MeshHopCountResponse[];
  readonly unavailableSensorKeys: readonly string[];
  readonly disconnectedComponents: readonly MeshDisconnectedComponentResponse[];
}

export interface MeshSelectedTreeEdgeResponse {
  readonly linkId: string;
  readonly parentNodeKey: string;
  readonly childNodeKey: string;
  readonly simulatedEnergyCost: number;
}

export interface MeshMinimumSpanningTreeResponse {
  readonly graphVersion: number;
  readonly gatewayKey: string;
  /** Sum of selected backbone edge weights; NOT a single-device route cost. */
  readonly totalSimulatedEnergyCost: number;
  readonly registeredSensorCount: number;
  readonly reachableSensorCount: number;
  readonly disconnectedSensorCount: number;
  readonly unavailableSensorCount: number;
  readonly selectedEdgeCount: number;
  readonly reachedNodeKeys: readonly string[];
  readonly selectedEdges: readonly MeshSelectedTreeEdgeResponse[];
  readonly unavailableSensorKeys: readonly string[];
  readonly disconnectedComponents: readonly MeshDisconnectedComponentResponse[];
}

export interface MeshHopRouteResponse {
  readonly status: MeshRouteStatus;
  readonly hopCount: number | null;
  /** Sensor first, logical gateway last. Empty if no eligible path exists. */
  readonly pathToGateway: readonly string[];
}

export interface MeshTreeRouteResponse {
  readonly status: MeshRouteStatus;
  readonly hopCount: number | null;
  /** Sum of weights along the selected Prim tree route, not a Dijkstra cost. */
  readonly accumulatedSimulatedEnergyCost: number | null;
  readonly pathToGateway: readonly string[];
  readonly selectedLinkIdsToGateway: readonly string[];
}

export interface MeshDeviceRouteResponse {
  readonly graphVersion: number;
  readonly sensorProfileId: string;
  readonly status: MeshRouteStatus;
  readonly bfs: MeshHopRouteResponse;
  readonly mst: MeshTreeRouteResponse;
}

/**
 * Atomically publish this object to React only after all separate GETs have
 * been checked for a common authoritative graph revision.
 */
export interface ConsistentTopologyProjection {
  readonly graphVersion: number;
  readonly topology: MeshTopologyResponse;
  readonly reachability: MeshReachabilityResponse;
  readonly mst: MeshMinimumSpanningTreeResponse;
  readonly selectedRoute: MeshDeviceRouteResponse | null;
}

export function getMeshTopology(signal?: AbortSignal): Promise<MeshTopologyResponse> {
  return getJson<MeshTopologyResponse>('/api/topology', signal);
}

export function getMeshReachability(signal?: AbortSignal): Promise<MeshReachabilityResponse> {
  return getJson<MeshReachabilityResponse>('/api/topology/reachability', signal);
}

export function getMeshMinimumSpanningTree(
  signal?: AbortSignal,
): Promise<MeshMinimumSpanningTreeResponse> {
  return getJson<MeshMinimumSpanningTreeResponse>('/api/topology/mst', signal);
}

export function getMeshDeviceRoute(
  sensorProfileId: string,
  signal?: AbortSignal,
): Promise<MeshDeviceRouteResponse> {
  return getJson<MeshDeviceRouteResponse>(
    `/api/topology/devices/${encodeURIComponent(sensorProfileId)}/route`,
    signal,
  );
}

/**
 * Independent HTTP calls can straddle a link/status mutation. Rather than
 * displaying a mixture of old BFS routes, new MST branches and old metadata,
 * retry a bounded number of times until all projections agree on one revision.
 *
 * The selected sensor is deliberately not cleared if it disappears from the
 * current graph. It belongs to the shared three-pillar workspace; the view
 * may explain that the selection is no longer registered.
 *
 * A 404 during route retrieval can mean that a sensor was removed between the
 * first topology GET and its route GET. Retry instead of inventing a route.
 */
export async function loadConsistentTopology(
  selectedSensorId: string | null,
  signal?: AbortSignal,
): Promise<ConsistentTopologyProjection> {
  const maximumAttempts = 3;

  for (let attempt = 0; attempt < maximumAttempts; attempt += 1) {
    signal?.throwIfAborted();

    const [topology, reachability, mst] = await Promise.all([
      getMeshTopology(signal),
      getMeshReachability(signal),
      getMeshMinimumSpanningTree(signal),
    ]);

    const graphVersion = topology.graphVersion;
    const consistent =
      graphVersion === reachability.graphVersion &&
      graphVersion === mst.graphVersion &&
      topology.gatewayKey === reachability.gatewayKey &&
      topology.gatewayKey === mst.gatewayKey;

    if (!consistent) {
      continue;
    }

    let selectedRoute: MeshDeviceRouteResponse | null = null;
    const requestedId = selectedSensorId?.toLowerCase();
    const sensorIsRegistered =
      requestedId !== undefined &&
      topology.nodes.some((node) => node.sensorProfileId?.toLowerCase() === requestedId);

    if (sensorIsRegistered && selectedSensorId !== null) {
      try {
        selectedRoute = await getMeshDeviceRoute(selectedSensorId, signal);
      } catch (error: unknown) {
        if (error instanceof ApiError && error.status === 404) {
          continue;
        }

        throw error;
      }

      if (
        selectedRoute.graphVersion !== graphVersion ||
        selectedRoute.sensorProfileId.toLowerCase() !== requestedId ||
        selectedRoute.bfs.status !== selectedRoute.mst.status
      ) {
        continue;
      }
    }

    signal?.throwIfAborted();
    return { graphVersion, topology, reachability, mst, selectedRoute };
  }

  throw new Error(
    'The mesh changed during the topology request. Refresh to retrieve a consistent graph revision.',
  );
}
