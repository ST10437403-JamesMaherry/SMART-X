import type {
  ConsistentTopologyProjection,
  MeshLinkResponse,
  MeshVertexResponse,
} from '../../api/topologyApi';

/**
 * Positions describe a deterministic *logical* diagram, never geographical or
 * measured radio coordinates. The bounded layout keeps complex graphs usable.
 */
export const MESH_DIAGRAM_WIDTH = 960;
export const MESH_DIAGRAM_HEIGHT = 520;
export const MESH_DIAGRAM_SENSOR_LIMIT = 25;

export type MeshConnection = 'Gateway' | 'Reachable' | 'Disconnected' | 'Unavailable';

export interface MeshDiagramNode {
  readonly node: MeshVertexResponse;
  readonly x: number;
  readonly y: number;
  readonly label: string;
  readonly connection: MeshConnection;
  readonly hopCount: number | null;
}

export interface MeshDiagramLayout {
  readonly nodes: readonly MeshDiagramNode[];
  readonly links: readonly MeshLinkResponse[];
  readonly matchingSensorCount: number;
  readonly hiddenSensorCount: number;
}

/** Exact key comparison is safe for canonical backend-generated mesh keys. */
export function sameMeshKey(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

export function getMeshConnection(
  node: MeshVertexResponse,
  projection: ConsistentTopologyProjection,
): MeshConnection {
  if (node.kind === 'Gateway') {
    return 'Gateway';
  }

  if (!node.isForwardingEligible) {
    return 'Unavailable';
  }

  return projection.reachability.reachableNodes.some((entry) =>
    sameMeshKey(entry.nodeKey, node.key),
  )
    ? 'Reachable'
    : 'Disconnected';
}

/**
 * One search field deliberately covers the real registration identity fields.
 * We never synthesize display names, MAC addresses, or vertex identifiers.
 */
export function matchesMeshSearch(node: MeshVertexResponse, search: string): boolean {
  const term = search.trim().toLocaleLowerCase();
  if (term.length === 0) {
    return true;
  }

  return [node.name, node.deviceIdentifier, node.sensorProfileId, node.key, node.category]
    .filter((value): value is string => typeof value === 'string')
    .some((value) => value.toLocaleLowerCase().includes(term));
}

/** Stable sort independent of API response insertion order or browser locale. */
export function sortMeshSensors(
  nodes: readonly MeshVertexResponse[],
): readonly MeshVertexResponse[] {
  return [...nodes].sort((left, right) => {
    const a = `${left.name ?? ''}\u0000${left.key}`.toLowerCase();
    const b = `${right.name ?? ''}\u0000${right.key}`.toLowerCase();
    return a < b ? -1 : a > b ? 1 : 0;
  });
}

/**
 * Selected nodes stay visible when they match the current search, even if
 * thousands of other sensors share the graph. The accessible table can still
 * navigate every matching registration without rendering them all as SVG.
 */
export function buildMeshDiagram(
  projection: ConsistentTopologyProjection,
  matchingSensors: readonly MeshVertexResponse[],
  selectedSensorId: string | null,
): MeshDiagramLayout {
  const sorted = sortMeshSensors(matchingSensors);
  const visible = sorted.slice(0, MESH_DIAGRAM_SENSOR_LIMIT);
  const selected = selectedSensorId
    ? sorted.find((node) => node.sensorProfileId?.toLowerCase() === selectedSensorId.toLowerCase())
    : undefined;

  if (selected && !visible.some((node) => sameMeshKey(node.key, selected.key))) {
    visible[visible.length - 1] = selected;
  }

  const gateway = projection.topology.nodes.find((node) => node.kind === 'Gateway');
  if (!gateway) {
    // The API contract always includes one logical gateway. A defensive empty
    // result is safer than generating a nonexistent vertex in the client.
    return {
      nodes: [],
      links: [],
      matchingSensorCount: sorted.length,
      hiddenSensorCount: sorted.length,
    };
  }

  const hopByKey = new Map(
    projection.reachability.reachableNodes.map((entry) => [
      entry.nodeKey.toLowerCase(),
      entry.hopCount,
    ]),
  );

  // Sort displayed vertices by authoritative hop level, then stable key. A
  // five-column grid prevents force-layout jitter and main-thread animation.
  const ordered = [...visible].sort((left, right) => {
    const leftHops = hopByKey.get(left.key.toLowerCase()) ?? Number.POSITIVE_INFINITY;
    const rightHops = hopByKey.get(right.key.toLowerCase()) ?? Number.POSITIVE_INFINITY;
    if (leftHops !== rightHops) {
      return leftHops - rightHops;
    }
    return left.key < right.key ? -1 : left.key > right.key ? 1 : 0;
  });

  const diagramNodes: MeshDiagramNode[] = [
    { node: gateway, x: 90, y: 260, label: 'GW', connection: 'Gateway', hopCount: 0 },
    ...ordered.map((node, index) => ({
      node,
      x: 245 + Math.floor(index / 5) * 147,
      y: 72 + (index % 5) * 94,
      label: String(index + 1).padStart(2, '0'),
      connection: getMeshConnection(node, projection),
      hopCount: hopByKey.get(node.key.toLowerCase()) ?? null,
    })),
  ];

  const drawnKeys = new Set(diagramNodes.map(({ node }) => node.key.toLowerCase()));
  const drawnLinks = projection.topology.links.filter(
    (link) =>
      drawnKeys.has(link.nodeAKey.toLowerCase()) && drawnKeys.has(link.nodeBKey.toLowerCase()),
  );

  return {
    nodes: diagramNodes,
    links: drawnLinks,
    matchingSensorCount: sorted.length,
    hiddenSensorCount: sorted.length - visible.length,
  };
}
