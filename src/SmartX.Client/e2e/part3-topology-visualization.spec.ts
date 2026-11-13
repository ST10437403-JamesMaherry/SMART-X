import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';
import type { MeshTopologyResponse } from '../src/api/topologyApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** The test creates actual registered Smart-X devices: never mocked nodes. */
async function registerOnline(request: APIRequestContext, name: string): Promise<SensorResponse> {
  const unique = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name,
      deviceIdentifier: `mesh-visual-${unique}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-08 Visual Test Facility',
        zone: 'Logical Graph Test Zone',
        nodeId: `visual-${unique}`,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });
  expect(response.status()).toBe(201);
  const sensor = (await response.json()) as SensorResponse;
  const online = await request.patch(`${apiBaseUrl}/api/sensors/${sensor.id}/status`, {
    data: { status: 'Online' },
  });
  expect(online.status()).toBe(200);
  return sensor;
}

async function sensorKey(request: APIRequestContext, sensorId: string): Promise<string> {
  const response = await request.get(`${apiBaseUrl}/api/topology`);
  expect(response.ok()).toBeTruthy();
  const graph = (await response.json()) as MeshTopologyResponse;
  const vertex = graph.nodes.find((node) => node.sensorProfileId === sensorId);
  expect(vertex, `Registration ${sensorId} should exist as a graph vertex`).toBeDefined();
  return vertex!.key;
}

async function addRealLink(
  request: APIRequestContext,
  first: string,
  second: string,
  cost: number,
): Promise<string> {
  const response = await request.post(`${apiBaseUrl}/api/topology/links`, {
    data: {
      nodeAKey: first,
      nodeBKey: second,
      simulatedEnergyCost: cost,
    },
  });
  expect(response.status()).toBe(201);
  const link = (await response.json()) as { id: string };
  return link.id;
}

test.describe('P3-08 accessible interactive mesh visualization', () => {
  test('renders only configured HTTP links and selects a real sensor from the diagram', async ({
    page,
    request,
  }) => {
    const prefix = `P3-08 Diagram ${randomUUID()}`;
    const first = await registerOnline(request, `${prefix} Relay`);
    const second = await registerOnline(request, `${prefix} Probe`);
    const firstKey = await sensorKey(request, first.id);
    const secondKey = await sensorKey(request, second.id);
    const gatewayFirst = await addRealLink(request, 'gateway', firstKey, 1.25);
    const firstSecond = await addRealLink(request, firstKey, secondKey, 2.5);

    await page.goto('/topology');
    await expect(page.getByRole('heading', { name: 'Explore the mesh' })).toBeVisible();
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(prefix);

    // The diagram includes one gateway and two registered sensors, with only
    // those configured edges. Cost text originates from actual API responses.
    const edges = page.getByTestId('mesh-diagram-edge');
    await expect(edges).toHaveCount(2);
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${gatewayFirst}"]`),
    ).toHaveCount(1);
    await expect(
      page.locator(`[data-testid="mesh-diagram-edge"][data-link-id="${firstSecond}"]`),
    ).toHaveCount(1);
    await expect(page.getByText('Showing 2 of 2 matching sensors and 2 links')).toBeVisible();

    const nodeButton = page.getByRole('button', {
      name: new RegExp(`Select node ${prefix} Probe`),
    });
    await nodeButton.click();

    await expect(page.getByRole('heading', { name: `${prefix} Probe` })).toBeVisible();
    await expect(page.locator('.mesh-details')).toContainText(second.deviceIdentifier);
    await expect(page.locator('.mesh-details')).toContainText('Included in gateway backbone');
    await expect(page.locator('.mesh-details')).toContainText('3.75');
    await expect(
      page.getByRole('list', { name: 'BFS route from selected sensor to gateway' }),
    ).toContainText(`${prefix} Relay`);
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(second.id);
  });

  test('searches authentic identifiers and provides a semantic table alternative', async ({
    page,
    request,
  }) => {
    const name = `P3-08 Table ${randomUUID()}`;
    const device = await registerOnline(request, name);

    await page.goto('/topology');
    await page
      .getByRole('searchbox', { name: 'Search mesh devices' })
      .fill(device.deviceIdentifier);
    await page.getByRole('button', { name: 'Accessible table' }).click();

    await expect(page.getByRole('button', { name: 'Accessible table' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    const table = page.getByRole('table');
    await expect(table.getByRole('row')).toHaveCount(2);
    await expect(table).toContainText(device.deviceIdentifier);
    await expect(table).toContainText('Disconnected');

    await table.getByRole('button', { name: `Select ${name}` }).click();
    await expect(page.getByRole('heading', { name })).toBeVisible();
    await expect(page.locator('.mesh-details')).toContainText('No gateway route');
    await page.getByRole('button', { name: 'Graph view' }).click();
    await expect(page.getByRole('button', { name: 'Graph view' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(
      page.getByRole('button', { name: new RegExp(`Select node ${name}`) }),
    ).toHaveAttribute('aria-pressed', 'true');
  });

  test('supports mobile width and keyboard-only sensor selection', async ({ page, request }) => {
    const name = `P3-08 Mobile ${randomUUID()}`;
    const sensor = await registerOnline(request, name);
    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/topology');
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(name);

    const sensorButton = page.getByRole('button', { name: new RegExp(`Select node ${name}`) });
    await sensorButton.focus();
    await expect(sensorButton).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.getByRole('heading', { name })).toBeVisible();

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);
    await expect(page.getByRole('region', { name: 'Scrollable mesh diagram' })).toBeVisible();
  });

  test('caps diagram nodes but keeps the complete result searchable and pageable', async ({
    page,
    request,
  }) => {
    test.setTimeout(120_000);
    const prefix = `P3-08 Bounded ${randomUUID()}`;
    for (let index = 0; index < 27; index += 1) {
      await registerOnline(request, `${prefix} Sensor ${String(index + 1).padStart(2, '0')}`);
    }

    await page.goto('/topology');
    await page.getByRole('searchbox', { name: 'Search mesh devices' }).fill(prefix);
    await expect(page.getByText(/27 of \d+ registered sensors match your search/)).toBeVisible();
    await expect(page.locator('.mesh-canvas__node-button')).toHaveCount(25);
    await expect(page.locator('.mesh-canvas__footnote')).toContainText(
      '2 sensors are outside this bounded diagram',
    );

    await page.getByRole('button', { name: 'Accessible table' }).click();
    await expect(page.getByRole('table').getByRole('row')).toHaveCount(21);
    await page.getByRole('button', { name: 'Next' }).click();
    await expect(page.getByText('Page 2 of 2')).toBeVisible();
    await expect(page.getByRole('table').getByRole('row')).toHaveCount(8);
  });
});
