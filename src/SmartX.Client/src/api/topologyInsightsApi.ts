import { getJson } from './apiClient';
import { getMeshTopology, type MeshVertexResponse } from './topologyApi';

/**
 * P3-10 read-only contract from SmartX.Contracts.Topology.
 * Rule-derived network facts are NEVER learned suggestions or permissions to
 * run commands. The backend owns BFS, graph revision and recovery lifecycle.
 */
export interface TopologyInsightResponse {
  readonly key: string;
  readonly kind: 'GatewayDisconnected' | 'NonForwardingDevices' | 'GatewayConnectivityRestored';
  readonly severity: 'Critical' | 'Warning' | 'Info';
  readonly source: string;
  readonly isLearned: boolean;
  readonly isCurrent: boolean;
  readonly title: string;
  readonly explanation: string;
  readonly recommendedAction: string;
  readonly evidenceGraphVersion: number;
  readonly firstObservedAtUtc: string;
  readonly lastConfirmedAtUtc: string;
  readonly expiresAtUtc: string | null;
  readonly affectedRegisteredSensorCount: number;
  readonly affectedSensorIds: readonly string[];
  readonly additionalAffectedSensorCount: number;
}

export interface TopologyInsightsResponse {
  readonly graphVersion: number;
  readonly evaluatedAtUtc: string;
  readonly projectionComputedAtUtc: string;
  readonly registeredSensorCount: number;
  readonly reachableSensorCount: number;
  readonly disconnectedSensorCount: number;
  readonly unavailableSensorCount: number;
  readonly insights: readonly TopologyInsightResponse[];
}

export interface VerifiedTopologyInsights {
  readonly response: TopologyInsightsResponse;
  readonly nodes: readonly MeshVertexResponse[];
}

/** Passive GET: neither interaction training nor actuator commands. */
export async function getTopologyInsights(signal?: AbortSignal): Promise<TopologyInsightsResponse> {
  const response = await getJson<TopologyInsightsResponse>('/api/topology/insights', signal);

  // Fail closed if the server ever changes provenance or bounds. A recovery
  // observation may legitimately cite the older transition graph revision.
  if (
    response.insights.length > 3 ||
    response.insights.some(
      (insight) =>
        insight.isLearned ||
        (insight.isCurrent && insight.evidenceGraphVersion !== response.graphVersion),
    )
  ) {
    throw new Error('Unverified insight provenance or graph revision.');
  }
  return response;
}

/**
 * Never attach a sensor's display name to an insight from a different graph
 * revision. Topology has a committed graph snapshot already; Operations reads
 * a matching snapshot because it does not otherwise hold topology data.
 *
 * The three-attempt cap avoids an unbounded loop during rapid edits. The API
 * service caches its BFS calculation per revision; this code does no BFS.
 */
export async function loadVerifiedTopologyInsights(
  expectedGraphVersion: number | null,
  knownNodes: readonly MeshVertexResponse[] | null,
  signal?: AbortSignal,
): Promise<VerifiedTopologyInsights> {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    signal?.throwIfAborted();
    if (expectedGraphVersion !== null && knownNodes !== null) {
      const response = await getTopologyInsights(signal);
      if (response.graphVersion === expectedGraphVersion) {
        return { response, nodes: knownNodes };
      }
      continue;
    }

    // A shared process-local graph can change between the two HTTP requests.
    const [response, topology] = await Promise.all([
      getTopologyInsights(signal),
      getMeshTopology(signal),
    ]);
    if (response.graphVersion === topology.graphVersion) {
      return { response, nodes: topology.nodes };
    }
  }

  throw new Error(
    'The network changed during insight evaluation. Refresh for a consistent revision.',
  );
}
