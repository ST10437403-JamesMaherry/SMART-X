import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse, SensorStatus } from '../src/api/sensorsApi';
import type {
  MeshDeviceRouteResponse,
  MeshLinkResponse,
  MeshTopologyResponse,
} from '../src/api/topologyApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/**
 * These helpers use the actual Development API started by playwright.config.ts.
 * Every test receives unique sensor IDs, so it never relies on an empty shared
 * in-memory registry or fabricated browser-side links.
 */
async function registerOnlineSensor(
  request: APIRequestContext,
  name: string,
): Promise<SensorResponse> {
  const unique = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name,
      deviceIdentifier: `p3-08-final-${unique}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-08 Final Regression Greenhouse',
        zone: 'End-to-end mesh verification',
        nodeId: unique,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });
  expect(response.status()).toBe(201);

  const sensor = (await response.json()) as SensorResponse;
  await setSensorStatus(request, sensor.id, 'Online');
  return sensor;
}

async function setSensorStatus(
  request: APIRequestContext,
  sensorId: string,
  status: SensorStatus,
): Promise<void> {
  const response = await request.patch(`${apiBaseUrl}/api/sensors/${sensorId}/status`, {
    data: { status },
  });
  expect(response.status()).toBe(200);
}

async function currentTopology(request: APIRequestContext): Promise<MeshTopologyResponse> {
  const response = await request.get(`${apiBaseUrl}/api/topology`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as MeshTopologyResponse;
}

async function keyForSensor(request: APIRequestContext, id: string): Promise<string> {
  const vertex = (await currentTopology(request)).nodes.find((node) => node.sensorProfileId === id);
  expect(vertex, `Registered sensor ${id} must have an authoritative graph key`).toBeDefined();
  return vertex!.key;
}

async function connect(
  request: APIRequestContext,
  first: string,
  second: string,
  cost: number,
): Promise<MeshLinkResponse> {
  const response = await request.post(`${apiBaseUrl}/api/topology/links`, {
    data: {
      nodeAKey: first,
      nodeBKey: second,
      simulatedEnergyCost: cost,
    },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as MeshLinkResponse;
}

async function serverRoute(
  request: APIRequestContext,
  sensorId: string,
): Promise<MeshDeviceRouteResponse> {
  const response = await request.get(`${apiBaseUrl}/api/topology/devices/${sensorId}/route`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as MeshDeviceRouteResponse;
}

/**
 * A stable locator targets only the selected sensor's tree-path cost, not the
 * total MST backbone cost or the nearby configured-link cost labels.
 */
function selectedTreeCost(page: Page) {
  return page
    .locator('.mesh-details__facts > div')
    .filter({ hasText: 'MST selected-tree route cost' })
    .locator('dd');
}

async function openFilteredTopology(page: Page, prefix: string): Promise<void> {
  await page.goto('/topology');
  await expect(page.getByRole('heading', { name: 'Explore the mesh' })).toBeVisible();
  await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(prefix);
}

function diagramEdge(page: Page, id: string) {
  return page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${id}"]`);
}

test.describe('P3-08 final accessible topology and rerouting regression', () => {
  test('supports keyboard-only graph and table selection with explicit overlay semantics', async ({
    page,
    request,
  }) => {
    const name = `P3-08 Keyboard ${randomUUID()}`;
    const sensor = await registerOnlineSensor(request, name);
    await connect(request, 'gateway', await keyForSensor(request, sensor.id), 1.25);

    await openFilteredTopology(page, name);

    // The semantic view buttons can be activated with Space, and expose state
    // to assistive technology through aria-pressed (not just visual styling).
    const tableButton = page.getByRole('button', { name: 'Accessible table' });
    await tableButton.focus();
    await expect(tableButton).toBeFocused();
    await page.keyboard.press('Space');
    await expect(tableButton).toHaveAttribute('aria-pressed', 'true');

    const table = page.getByRole('table');
    await expect(table).toContainText(name);
    const selectRow = table.getByRole('button', { name: `Select ${name}` });
    await selectRow.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.getByRole('heading', { name })).toBeVisible();
    await expect(selectedTreeCost(page)).toHaveText('1.25');

    const graphButton = page.getByRole('button', { name: 'Graph view' });
    await graphButton.focus();
    await page.keyboard.press('Enter');
    await expect(graphButton).toHaveAttribute('aria-pressed', 'true');

    // Clear then reselect through the native node button with the keyboard.
    await page.getByLabel('Select a registered sensor').selectOption('');
    const node = page.getByRole('button', { name: new RegExp(`Select node ${name}`) });
    await node.focus();
    await expect(node).toBeFocused();
    await page.keyboard.press('Space');
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(node).toHaveAttribute('aria-pressed', 'true');

    const bfs = page.getByRole('button', { name: 'BFS / Fewest-hop route' });
    await bfs.focus();
    await page.keyboard.press('Enter');
    await expect(bfs).toHaveAttribute('aria-pressed', 'true');
    await expect(
      page.getByRole('list', { name: 'BFS route from selected sensor to gateway' }),
    ).toContainText('Gateway');
    await expect(page.locator('[aria-label="Network node legend"]')).toBeVisible();
    await expect(page.locator('[aria-label="Line style legend"]')).toBeVisible();
  });

  test('live primary-relay failure switches to the backup MST route and recovers without a reload', async ({
    page,
    request,
  }) => {
    const prefix = `P3-08 Failover ${randomUUID()}`;
    const primary = await registerOnlineSensor(request, `${prefix} Primary`);
    const backup = await registerOnlineSensor(request, `${prefix} Backup`);
    const leaf = await registerOnlineSensor(request, `${prefix} Leaf`);
    const primaryKey = await keyForSensor(request, primary.id);
    const backupKey = await keyForSensor(request, backup.id);
    const leafKey = await keyForSensor(request, leaf.id);

    const gatewayPrimary = await connect(request, 'gateway', primaryKey, 1);
    const primaryLeaf = await connect(request, primaryKey, leafKey, 1);
    const gatewayBackup = await connect(request, 'gateway', backupKey, 4);
    const backupLeaf = await connect(request, backupKey, leafKey, 1);

    await openFilteredTopology(page, prefix);
    await page.getByRole('button', { name: new RegExp(`Select node ${prefix} Leaf`) }).click();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);
    await expect(selectedTreeCost(page)).toHaveText('2.00');
    await page.getByRole('button', { name: 'Prim / Minimum-cost backbone' }).click();
    await expect(diagramEdge(page, gatewayPrimary.id)).toHaveAttribute(
      'data-overlay-state',
      'mst-backbone',
    );
    await expect(diagramEdge(page, gatewayBackup.id)).toHaveAttribute(
      'data-overlay-state',
      'context',
    );
    await expect(page.getByText('Realtime channel: connected', { exact: false })).toBeVisible();

    // Change a *different* device directly through the actual backend. The
    // mounted Topology page must react to SignalR invalidation, not a click on
    // Refresh or a fabricated React-only network mutation.
    await setSensorStatus(request, primary.id, 'Offline');
    const failedVersion = (await currentTopology(request)).graphVersion;
    await expect(page.locator('p.topology-bootstrap__meta')).toContainText(
      `Graph revision ${failedVersion} ·`,
      { timeout: 30_000 },
    );
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);
    await expect(selectedTreeCost(page)).toHaveText('5.00');
    await expect(diagramEdge(page, primaryLeaf.id)).toHaveAttribute(
      'data-overlay-state',
      'ineligible',
    );
    await expect(diagramEdge(page, gatewayBackup.id)).toHaveAttribute(
      'data-overlay-state',
      'mst-backbone',
    );
    await expect(diagramEdge(page, backupLeaf.id)).toHaveAttribute(
      'data-overlay-state',
      'mst-backbone',
    );
    await expect(
      page.getByRole('list', { name: 'MST tree route from selected sensor to gateway' }),
    ).toContainText(`${prefix} Backup`);
    expect((await serverRoute(request, leaf.id)).mst.accumulatedSimulatedEnergyCost).toBe(5);

    await setSensorStatus(request, primary.id, 'Online');
    const restoredVersion = (await currentTopology(request)).graphVersion;
    await expect(page.locator('p.topology-bootstrap__meta')).toContainText(
      `Graph revision ${restoredVersion} ·`,
      { timeout: 30_000 },
    );
    await expect(selectedTreeCost(page)).toHaveText('2.00');
    await expect(diagramEdge(page, gatewayPrimary.id)).toHaveAttribute(
      'data-overlay-state',
      'mst-backbone',
    );
    await expect(diagramEdge(page, gatewayBackup.id)).toHaveAttribute(
      'data-overlay-state',
      'context',
    );
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);
  });

  test('shows explicit disconnection when the only relay fails, without deleting configured links', async ({
    page,
    request,
  }) => {
    const prefix = `P3-08 Single Bridge ${randomUUID()}`;
    const relay = await registerOnlineSensor(request, `${prefix} Relay`);
    const leaf = await registerOnlineSensor(request, `${prefix} Leaf`);
    const relayKey = await keyForSensor(request, relay.id);
    const leafKey = await keyForSensor(request, leaf.id);
    const first = await connect(request, 'gateway', relayKey, 1);
    const second = await connect(request, relayKey, leafKey, 1);

    await openFilteredTopology(page, prefix);
    await page.getByRole('button', { name: new RegExp(`Select node ${prefix} Leaf`) }).click();
    await page.getByRole('button', { name: 'BFS / Fewest-hop route' }).click();
    await expect(diagramEdge(page, first.id)).toHaveAttribute('data-overlay-state', 'bfs-route');
    await expect(diagramEdge(page, second.id)).toHaveAttribute('data-overlay-state', 'bfs-route');
    await expect(page.getByText('Realtime channel: connected', { exact: false })).toBeVisible();

    await setSensorStatus(request, relay.id, 'Maintenance');
    const failedVersion = (await currentTopology(request)).graphVersion;
    await expect(page.locator('p.topology-bootstrap__meta')).toContainText(
      `Graph revision ${failedVersion} ·`,
      { timeout: 30_000 },
    );
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);
    await expect(page.locator('.mesh-details__state')).toContainText('Disconnected');
    await expect(selectedTreeCost(page)).toHaveText('No gateway route');
    await expect(page.locator('.mesh-canvas__overlay-summary')).toContainText('disconnected');
    await expect(diagramEdge(page, first.id)).toHaveAttribute('data-overlay-state', 'ineligible');
    await expect(diagramEdge(page, second.id)).toHaveAttribute('data-overlay-state', 'ineligible');
    const failedGraph = await currentTopology(request);
    expect(failedGraph.links.some((edge) => edge.id === first.id)).toBe(true);
    expect(failedGraph.links.some((edge) => edge.id === second.id)).toBe(true);
    expect((await serverRoute(request, leaf.id)).status).toBe('Disconnected');

    // Warning is forwarding-eligible in Smart-X; recovery must restore the
    // exact configured links, not secretly recreate them client-side.
    await setSensorStatus(request, relay.id, 'Warning');
    const recoveredVersion = (await currentTopology(request)).graphVersion;
    await expect(page.locator('p.topology-bootstrap__meta')).toContainText(
      `Graph revision ${recoveredVersion} ·`,
      { timeout: 30_000 },
    );
    await expect(diagramEdge(page, first.id)).toHaveAttribute('data-overlay-state', 'bfs-route');
    await expect(diagramEdge(page, second.id)).toHaveAttribute('data-overlay-state', 'bfs-route');
    await expect(selectedTreeCost(page)).toHaveText('2.00');
  });

  test('preserves selection and observes missed health changes after navigating away and reconnecting', async ({
    page,
    request,
  }) => {
    const name = `P3-08 Return ${randomUUID()}`;
    const sensor = await registerOnlineSensor(request, name);
    await connect(request, 'gateway', await keyForSensor(request, sensor.id), 2.75);

    await openFilteredTopology(page, name);
    await page.getByLabel('Select a registered sensor').selectOption(sensor.id);
    await expect(selectedTreeCost(page)).toHaveText('2.75');
    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await expect(page).toHaveURL(/\/ingestion$/);

    // Topology's SignalR hook is now unmounted, so this event was missed by
    // the page. Returning reestablishes the subscription AND loads fresh HTTP
    // data; the cross-pillar workspace still holds the same selected ID.
    await setSensorStatus(request, sensor.id, 'Fault');
    await page.goBack();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.locator('.mesh-details__state')).toContainText('Unavailable');
    await expect(selectedTreeCost(page)).toHaveText('No gateway route');
    await expect(page.getByText('Realtime channel: connected', { exact: false })).toBeVisible({
      timeout: 30_000,
    });
  });

  test('keeps the mobile page within 375px while graph scrolls locally and simulation controls remain usable', async ({
    page,
    request,
  }) => {
    const prefix = `P3-08 Narrow ${randomUUID()}`;
    const sensor = await registerOnlineSensor(request, `${prefix} Probe`);
    await connect(request, 'gateway', await keyForSensor(request, sensor.id), 1.5);

    await page.setViewportSize({ width: 375, height: 812 });
    await openFilteredTopology(page, prefix);
    await page.getByLabel('Select a registered sensor').selectOption(sensor.id);
    await page.getByRole('button', { name: 'BFS / Fewest-hop route' }).click();
    await expect(page.locator('.mesh-canvas__overlay-summary')).toContainText('1 hop');
    await expect(page.getByRole('region', { name: 'Scrollable mesh diagram' })).toBeVisible();

    const widths = await page.evaluate(() => {
      const viewport = document.querySelector('.mesh-canvas__viewport');
      return {
        pageOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        diagramScrollWidth: viewport?.scrollWidth ?? 0,
        diagramClientWidth: viewport?.clientWidth ?? 0,
      };
    });
    expect(widths.pageOverflow).toBeLessThanOrEqual(1);
    expect(widths.diagramScrollWidth).toBeGreaterThan(widths.diagramClientWidth);

    await page.getByRole('button', { name: 'Accessible table' }).click();
    await expect(page.getByRole('table')).toContainText(`${prefix} Probe`);
    await expect(page.getByRole('button', { name: 'BFS / Fewest-hop route' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.getByRole('form', { name: 'Create simulated mesh link' })).toBeVisible();

    const tableOverflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(tableOverflow).toBeLessThanOrEqual(1);
  });

  test('keeps a selected sensor beyond the 25-node diagram limit reachable through the paginated table', async ({
    page,
    request,
  }) => {
    test.setTimeout(120_000);
    const prefix = `P3-08 Deep Table ${randomUUID()}`;
    const sensors: SensorResponse[] = [];
    for (let index = 1; index <= 27; index += 1) {
      sensors.push(
        await registerOnlineSensor(request, `${prefix} Sensor ${String(index).padStart(2, '0')}`),
      );
    }
    const last = sensors[26];

    await openFilteredTopology(page, prefix);
    await expect(page.locator('.mesh-canvas__node-button')).toHaveCount(25);
    await expect(page.locator('.mesh-canvas__footnote')).toContainText(
      '2 sensors are outside this bounded diagram',
    );
    await page.getByRole('button', { name: 'Accessible table' }).click();
    await expect(page.getByRole('table').getByRole('row')).toHaveCount(21);
    await page.getByRole('button', { name: 'Next' }).click();
    await expect(page.getByText('Page 2 of 2')).toBeVisible();
    await page
      .getByRole('table')
      .getByRole('button', { name: `Select ${prefix} Sensor 27` })
      .click();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(last.id);
    await page.getByRole('button', { name: 'Graph view' }).click();
    await expect(page.locator('.mesh-canvas__node-button')).toHaveCount(25);
    await expect(
      page.getByRole('button', { name: new RegExp(`Select node ${prefix} Sensor 27`) }),
    ).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.mesh-canvas__footnote')).toContainText(
      '2 sensors are outside this bounded diagram',
    );
  });
});
