import {
  createMeshLink,
  getMeshTopology,
  removeMeshLink,
  updateMeshLinkCost,
  type MeshTopologyResponse,
} from '../../api/topologyApi';
import {
  changeSensorStatus,
  getSensors,
  registerSensor,
  type RegisterSensorRequest,
  type SensorResponse,
} from '../../api/sensorsApi';

/**
 * The greenhouse fixture matches MeshGreenhouseEndToEndScenarioTests exactly.
 * All writes go through existing validated HTTP endpoints.
 */
export type ScenarioId = 'healthy' | 'relay-failure' | 'island' | 'recovered';

interface DeviceDefinition {
  readonly id: string;
  readonly name: string;
  readonly category: 'Actuator' | 'Environmental';
  readonly suffix: string;
}

const devices: readonly DeviceDefinition[] = [
  { id: 'primary', name: 'Primary irrigation relay', category: 'Actuator', suffix: 'A1' },
  { id: 'backup', name: 'Backup irrigation relay', category: 'Actuator', suffix: 'A2' },
  { id: 'moisture', name: 'West-zone moisture probe', category: 'Environmental', suffix: 'A3' },
  { id: 'repeater', name: 'Central greenhouse repeater', category: 'Actuator', suffix: 'A4' },
  { id: 'nutrient', name: 'East-zone nutrient probe', category: 'Environmental', suffix: 'A5' },
  {
    id: 'vulnerable',
    name: 'Single-bridge moisture probe',
    category: 'Environmental',
    suffix: 'A6',
  },
];

type FixtureId = (typeof devices)[number]['id'] | 'gateway';

interface FixtureEdge {
  readonly first: FixtureId;
  readonly second: FixtureId;
  readonly cost: number;
}

const edges: readonly FixtureEdge[] = [
  { first: 'gateway', second: 'primary', cost: 1 },
  { first: 'gateway', second: 'backup', cost: 5 },
  { first: 'primary', second: 'moisture', cost: 1 },
  { first: 'moisture', second: 'repeater', cost: 1 },
  { first: 'repeater', second: 'backup', cost: 1 },
  { first: 'backup', second: 'nutrient', cost: 1.25 },
  { first: 'primary', second: 'vulnerable', cost: 0.5 },
];

export interface ScenarioDefinition {
  readonly id: ScenarioId;
  readonly title: string;
  readonly description: string;
  readonly reachable: number;
  readonly disconnected: number;
  readonly unavailable: number;
  readonly mstCost: number;
  readonly links: number;
}

export const scenarios: readonly ScenarioDefinition[] = [
  {
    id: 'healthy',
    title: '01 / Healthy network',
    description:
      'All six sensors are online. Compare the backup connection with the minimum-cost backbone.',
    reachable: 6,
    disconnected: 0,
    unavailable: 0,
    mstCost: 5.75,
    links: 7,
  },
  {
    id: 'relay-failure',
    title: '02 / Primary relay fails',
    description:
      'Take the primary relay offline. Four devices use the backup; one leaf becomes disconnected.',
    reachable: 4,
    disconnected: 1,
    unavailable: 1,
    mstCost: 8.25,
    links: 7,
  },
  {
    id: 'island',
    title: '03 / Gateway partition',
    description:
      'Keep the primary offline and remove the gateway backup link to isolate the sensor network.',
    reachable: 0,
    disconnected: 5,
    unavailable: 1,
    mstCost: 0,
    links: 6,
  },
  {
    id: 'recovered',
    title: '04 / Recover network',
    description:
      'Restore the primary relay and backup link. The original reachable set and backbone cost return.',
    reachable: 6,
    disconnected: 0,
    unavailable: 0,
    mstCost: 5.75,
    links: 7,
  },
];

export function fixtureIdentifier(device: DeviceDefinition): string {
  return `AA:BB:CC:EE:07:${device.suffix}`;
}

function pairKey(a: string, b: string): string {
  return [a.toLowerCase(), b.toLowerCase()].sort().join('|');
}

export class ScenarioSafetyError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ScenarioSafetyError';
  }
}

interface FixtureInspection {
  readonly existing: Map<string, SensorResponse>;
  readonly graph: MeshTopologyResponse;
}

async function inspectWorkspace(): Promise<FixtureInspection> {
  const [sensors, graph] = await Promise.all([getSensors(), getMeshTopology()]);
  const known = new Map(devices.map((device) => [fixtureIdentifier(device).toLowerCase(), device]));
  const existing = new Map<string, SensorResponse>();

  for (const sensor of sensors) {
    const fixture = known.get(sensor.deviceIdentifier.toLowerCase());
    if (
      !fixture ||
      existing.has(fixture.id) ||
      sensor.name !== fixture.name ||
      sensor.category !== fixture.category ||
      sensor.identifierKind !== 'MacAddress' ||
      sensor.telemetryDataType !== 'Float' ||
      sensor.deploymentLocation.facility !== 'Greenhouse' ||
      sensor.deploymentLocation.zone !== 'Hydroponics' ||
      sensor.deploymentLocation.nodeId !== `Zone-${fixture.suffix}` ||
      sensor.status === 'Decommissioned'
    ) {
      throw new ScenarioSafetyError(
        'This workspace contains other devices or modified demo sensors. The Simulation Lab will not overwrite it. Run the lab in a fresh local API instance.',
      );
    }
    existing.set(fixture.id, sensor);
  }

  const knownKeys = new Set(
    [...existing.values()].map((sensor) => `sensor/${sensor.id}`.toLowerCase()),
  );
  knownKeys.add('gateway');
  const expected = new Set(
    edges.map((edge) =>
      pairKey(
        edge.first === 'gateway' ? 'gateway' : `sensor/${existing.get(edge.first)?.id ?? ''}`,
        edge.second === 'gateway' ? 'gateway' : `sensor/${existing.get(edge.second)?.id ?? ''}`,
      ),
    ),
  );

  for (const link of graph.links) {
    if (
      !knownKeys.has(link.nodeAKey.toLowerCase()) ||
      !knownKeys.has(link.nodeBKey.toLowerCase()) ||
      !expected.has(pairKey(link.nodeAKey, link.nodeBKey))
    ) {
      throw new ScenarioSafetyError(
        'Unexpected mesh links found. The lab refuses to delete or modify unrelated topology.',
      );
    }
  }

  // Check all registered fixture sensors are actually present in the graph.
  for (const sensor of existing.values()) {
    if (
      !graph.nodes.some((node) => node.sensorProfileId?.toLowerCase() === sensor.id.toLowerCase())
    ) {
      throw new ScenarioSafetyError(
        'Sensor registry and mesh graph disagree. Retry after refresh.',
      );
    }
  }

  return { existing, graph };
}

function key(label: FixtureId, sensors: Map<string, SensorResponse>): string {
  if (label === 'gateway') return 'gateway';
  const sensor = sensors.get(label);
  if (!sensor) throw new Error(`Missing fixture sensor: ${label}`);
  return `sensor/${sensor.id}`;
}

async function ensureSensors(sensors: Map<string, SensorResponse>): Promise<void> {
  for (const device of devices) {
    if (sensors.has(device.id)) continue;
    const request: RegisterSensorRequest = {
      name: device.name,
      deviceIdentifier: fixtureIdentifier(device),
      identifierKind: 'MacAddress',
      deploymentLocation: {
        facility: 'Greenhouse',
        zone: 'Hydroponics',
        nodeId: `Zone-${device.suffix}`,
      },
      category: device.category,
      telemetryDataType: 'Float',
    };
    sensors.set(device.id, await registerSensor(request));
  }
}

async function ensureHealth(
  sensors: Map<string, SensorResponse>,
  label: string,
  status: 'Online' | 'Offline',
): Promise<void> {
  const record = sensors.get(label);
  if (!record) throw new Error(`Missing fixture sensor: ${label}`);
  if (record.status !== status) {
    sensors.set(label, await changeSensorStatus(record.id, status));
  }
}

async function reconcileLinks(
  sensors: Map<string, SensorResponse>,
  scenario: ScenarioId,
): Promise<void> {
  const graph = await getMeshTopology();
  const allFixtureKeys = new Set(
    edges.map((edge) => pairKey(key(edge.first, sensors), key(edge.second, sensors))),
  );
  const desired = new Map(
    edges
      .filter(
        (edge) =>
          scenario !== 'island' ||
          pairKey(edge.first, edge.second) !== pairKey('gateway', 'backup'),
      )
      .map((edge) => [pairKey(key(edge.first, sensors), key(edge.second, sensors)), edge]),
  );
  const existing = new Map<string, (typeof graph.links)[number]>();

  // Re-check after registration; concurrent edits must never be silently deleted.
  for (const link of graph.links) {
    const pair = pairKey(link.nodeAKey, link.nodeBKey);
    if (!allFixtureKeys.has(pair) || existing.has(pair)) {
      throw new ScenarioSafetyError('The mesh changed unexpectedly; refresh before retrying.');
    }
    existing.set(pair, link);
  }

  for (const [pair, link] of existing) {
    const expected = desired.get(pair);
    if (!expected) {
      await removeMeshLink(link.id);
    } else if (link.simulatedEnergyCost !== expected.cost) {
      await updateMeshLinkCost(link.id, expected.cost);
    }
  }

  for (const [pair, edge] of desired) {
    if (!existing.has(pair)) {
      await createMeshLink({
        nodeAKey: key(edge.first, sensors),
        nodeBKey: key(edge.second, sensors),
        simulatedEnergyCost: edge.cost,
      });
    }
  }
}

/**
 * Scenario changes are serialized by the dashboard. This is an intentionally
 * local-only operator workflow, not a remotely exposed administrative API.
 * Interrupted writes are not rolled back: the UI instructs the user to refresh.
 */
export async function applyScenario(id: ScenarioId): Promise<void> {
  const { existing } = await inspectWorkspace();
  await ensureSensors(existing);

  // Put all devices online before applying the failure. This permits a
  // transition from any earlier fixture scenario, including recovery.
  for (const device of devices) {
    await ensureHealth(existing, device.id, 'Online');
  }
  await reconcileLinks(existing, id);
  if (id === 'relay-failure' || id === 'island') {
    await ensureHealth(existing, 'primary', 'Offline');
  }
}

export function matchScenario(projection: {
  readonly topology: {
    readonly nodes: readonly {
      readonly deviceIdentifier: string | null;
      readonly status: string | null;
    }[];
    readonly linkCount: number;
  };
  readonly reachability: {
    readonly registeredSensorCount: number;
    readonly reachableSensorCount: number;
    readonly disconnectedSensorCount: number;
    readonly unavailableSensorCount: number;
  };
  readonly mst: { readonly totalSimulatedEnergyCost: number };
}): ScenarioDefinition | null {
  if (projection.reachability.registeredSensorCount !== devices.length) return null;
  if (
    !devices.every((device) =>
      projection.topology.nodes.some(
        (node) =>
          node.deviceIdentifier?.toLowerCase() === fixtureIdentifier(device).toLowerCase() &&
          node.status === (device.id === 'primary' ? 'Online' : 'Online'),
      ),
    )
  ) {
    // Failure scenarios are recognized separately below.
    const primary = devices[0];
    if (
      !devices.every((device) =>
        projection.topology.nodes.some(
          (node) =>
            node.deviceIdentifier?.toLowerCase() === fixtureIdentifier(device).toLowerCase() &&
            node.status === (device.id === primary.id ? 'Offline' : 'Online'),
        ),
      )
    )
      return null;
  }

  return (
    scenarios.find(
      (scenario) =>
        scenario.links === projection.topology.linkCount &&
        scenario.reachable === projection.reachability.reachableSensorCount &&
        scenario.disconnected === projection.reachability.disconnectedSensorCount &&
        scenario.unavailable === projection.reachability.unavailableSensorCount &&
        Math.abs(scenario.mstCost - projection.mst.totalSimulatedEnergyCost) < 0.000001 &&
        (scenario.id === 'healthy' || scenario.id === 'recovered'
          ? projection.topology.nodes.some(
              (node) =>
                node.deviceIdentifier?.toLowerCase() ===
                  fixtureIdentifier(devices[0]).toLowerCase() && node.status === 'Online',
            )
          : projection.topology.nodes.some(
              (node) =>
                node.deviceIdentifier?.toLowerCase() ===
                  fixtureIdentifier(devices[0]).toLowerCase() && node.status === 'Offline',
            )),
    ) ?? null
  );
}
