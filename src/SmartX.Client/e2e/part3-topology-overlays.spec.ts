import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';
import type { MeshLinkResponse, MeshTopologyResponse } from '../src/api/topologyApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** All test devices are registered through the real backend, never mocked. */
async function createSensor(request: APIRequestContext, name: string): Promise<SensorResponse> {
  const id = randomUUID();
  const register = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name,
      deviceIdentifier: `overlay-${id}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-08 Overlay Test Facility',
        zone: 'Real HTTP routing test',
        nodeId: id,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });
  expect(register.status()).toBe(201);
  const sensor = (await register.json()) as SensorResponse;
  const online = await request.patch(`${apiBaseUrl}/api/sensors/${sensor.id}/status`, {
    data: { status: 'Online' },
  });
  expect(online.status()).toBe(200);
  return sensor;
}

async function currentGraph(request: APIRequestContext): Promise<MeshTopologyResponse> {
  const response = await request.get(`${apiBaseUrl}/api/topology`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as MeshTopologyResponse;
}

async function vertexKey(request: APIRequestContext, sensorId: string): Promise<string> {
  const vertex = (await currentGraph(request)).nodes.find(
    (node) => node.sensorProfileId === sensorId,
  );
  expect(vertex).toBeDefined();
  return vertex!.key;
}

async function link(
  request: APIRequestContext,
  a: string,
  b: string,
  cost: number,
): Promise<MeshLinkResponse> {
  const response = await request.post(`${apiBaseUrl}/api/topology/links`, {
    data: { nodeAKey: a, nodeBKey: b, simulatedEnergyCost: cost },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as MeshLinkResponse;
}

test.describe('P3-08 real API mesh routing overlays and simulation controls', () => {
  test('highlights true BFS and Prim edges differently while preserving graph identity', async ({
    page,
    request,
  }) => {
    const prefix = `P3-08 Routes ${randomUUID()}`;
    const relay = await createSensor(request, `${prefix} Relay`);
    const probe = await createSensor(request, `${prefix} Probe`);
    const relayKey = await vertexKey(request, relay.id);
    const probeKey = await vertexKey(request, probe.id);
    const gatewayRelay = await link(request, 'gateway', relayKey, 1);
    const relayProbe = await link(request, relayKey, probeKey, 1);
    const direct = await link(request, 'gateway', probeKey, 9);

    await page.goto('/topology');
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(prefix);
    await expect(page.getByTestId('mesh-diagram-edge')).toHaveCount(3);
    await expect(page.getByRole('button', { name: 'All configured connections' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await page.getByRole('button', { name: new RegExp(`Select node ${prefix} Probe`) }).click();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(probe.id);
    await expect(page.locator('.mesh-details')).toContainText('BFS minimum hops');
    await expect(page.locator('.mesh-details')).toContainText('MST selected-tree route cost');

    await page.getByRole('button', { name: 'BFS / Fewest-hop route' }).click();
    await expect(page.getByRole('button', { name: 'BFS / Fewest-hop route' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${direct.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'bfs-route');
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${relayProbe.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'context');
    await expect(page.locator('.mesh-canvas__overlay-summary')).toContainText('1 hop');

    await page.getByRole('button', { name: 'Prim / Minimum-cost backbone' }).click();
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${gatewayRelay.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'mst-backbone');
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${relayProbe.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'mst-backbone');
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${direct.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'context');
    await expect(page.locator('.mesh-canvas__overlay-summary')).toContainText('minimum-total-cost');
  });

  test('uses the actual create, patch and delete link endpoints and reports only confirmed writes', async ({
    page,
    request,
  }) => {
    const name = `P3-08 Link Form ${randomUUID()}`;
    const sensor = await createSensor(request, name);
    const key = await vertexKey(request, sensor.id);

    await page.goto('/topology');
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(name);
    await page.getByLabel('First endpoint').selectOption('gateway');
    await page.getByLabel('Second endpoint').selectOption(key);
    await page.getByLabel('Relative energy cost').fill('2.50');
    await page.getByRole('button', { name: 'Create simulated link' }).click();
    await expect(page.getByTestId('topology-simulation-feedback')).toContainText(
      'Simulated link created',
    );

    const created = (await currentGraph(request)).links.find(
      (item) =>
        (item.nodeAKey === 'gateway' && item.nodeBKey === key) ||
        (item.nodeBKey === 'gateway' && item.nodeAKey === key),
    );
    expect(created).toBeDefined();
    expect(created!.simulatedEnergyCost).toBe(2.5);
    await expect(page.getByTestId('mesh-diagram-edge')).toHaveCount(1);

    await page.getByLabel('Configured connection').selectOption(created!.id);
    await page.getByLabel('New relative cost').fill('0.75');
    await page.getByRole('button', { name: 'Update link cost' }).click();
    await expect(page.getByTestId('topology-simulation-feedback')).toContainText('0.75');
    const afterPatch = (await currentGraph(request)).links.find((item) => item.id === created!.id);
    expect(afterPatch?.simulatedEnergyCost).toBe(0.75);

    await page.getByLabel('Configured connection').selectOption(created!.id);
    page.once('dialog', (dialog) => dialog.accept());
    await page.getByRole('button', { name: 'Remove link' }).click();
    await expect(page.getByTestId('topology-simulation-feedback')).toContainText(
      'Simulated link removed',
    );
    expect((await currentGraph(request)).links.some((item) => item.id === created!.id)).toBe(false);
    await expect(page.getByTestId('mesh-diagram-edge')).toHaveCount(0);
  });

  test('shows unavailable forwarding and recovers the same selected sensor after live status edits', async ({
    page,
    request,
  }) => {
    const name = `P3-08 Failure ${randomUUID()}`;
    const sensor = await createSensor(request, name);
    const key = await vertexKey(request, sensor.id);
    const direct = await link(request, 'gateway', key, 1.5);

    await page.goto('/topology');
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(name);
    await page.getByRole('button', { name: new RegExp(`Select node ${name}`) }).click();
    await page.getByRole('button', { name: 'BFS / Fewest-hop route' }).click();
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${direct.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'bfs-route');

    await page.getByLabel('New simulated health').selectOption('Offline');
    await page.getByRole('button', { name: 'Apply simulated status' }).click();
    await expect(page.getByTestId('topology-simulation-feedback')).toContainText('is Offline');
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.locator('.mesh-canvas__overlay-summary')).toContainText('unavailable');
    await expect(page.locator('.mesh-details')).toContainText('Unavailable');
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${direct.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'ineligible');

    await page.getByLabel('New simulated health').selectOption('Online');
    await page.getByRole('button', { name: 'Apply simulated status' }).click();
    await expect(page.getByTestId('topology-simulation-feedback')).toContainText('is Online');
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${direct.id}"]`),
    ).toHaveAttribute('data-overlay-state', 'bfs-route');
  });

  test('rejects invalid same-endpoint links without inventing a graph change', async ({
    page,
    request,
  }) => {
    const sensor = await createSensor(request, `P3-08 Invalid ${randomUUID()}`);
    const key = await vertexKey(request, sensor.id);
    await page.goto('/topology');
    await page.getByLabel('First endpoint').selectOption(key);
    await page.getByLabel('Second endpoint').selectOption(key);
    await page.getByRole('button', { name: 'Create simulated link' }).click();
    await expect(page.getByRole('alert')).toContainText('two different registered mesh vertices');
    const graph = await currentGraph(request);
    expect(graph.links.some((item) => item.nodeAKey === key && item.nodeBKey === key)).toBe(false);
  });
});
