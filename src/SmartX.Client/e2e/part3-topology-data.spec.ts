import { randomUUID } from 'node:crypto';

import { expect, test } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/**
 * P3-08 Commit 1 uses the actual isolated Playwright ASP.NET Core process.
 * No route responses, sensor names or mesh edges are mocked in the browser.
 */
test.describe('Part 3 live topology API foundation', () => {
  test('shows authoritative graph data and retains selected identity across pillars', async ({
    page,
    request,
  }) => {
    const unique = randomUUID();
    const sensorName = `Topology Contract ${unique}`;
    const registration = await request.post(`${apiBaseUrl}/api/sensors`, {
      data: {
        name: sensorName,
        deviceIdentifier: `topology-contract-${unique}`,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: {
          facility: 'Topology Test Facility',
          zone: 'Mesh Contract Zone',
          nodeId: `node-${unique}`,
        },
        category: 'Environmental',
        telemetryDataType: 'Float',
      },
    });

    expect(registration.status()).toBe(201);
    const sensor = (await registration.json()) as SensorResponse;

    await page.goto('/topology');
    await expect(page.getByRole('heading', { name: 'Live mesh topology' })).toBeVisible();
    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();
    await expect(page.locator('.topology-bootstrap__stats')).toContainText('Gateway reachable');

    // A newly registered sensor is present because the UI queried the
    // actual P3-07 graph. Registration does not invent wireless links.
    const selector = page.getByLabel('Select a registered sensor');
    await expect(selector.locator(`option[value="${sensor.id}"]`)).toHaveCount(1);
    await selector.selectOption(sensor.id);
    await expect(page.locator('.topology-bootstrap__selected')).toContainText(sensorName);
    await expect(page.locator('.topology-bootstrap__selected')).toContainText('Gateway route:');

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await expect(page).toHaveURL(/\/ingestion$/);
    await page.getByRole('link', { name: /Network Topology and Mesh Routing/ }).click();
    await expect(page).toHaveURL(/\/topology$/);

    // The provider above the routes, not a page-local state variable, owns
    // selectedSensorId and preserves it across client-side navigation.
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
  });

  test('refreshes real registrations and never overflows a 375px viewport', async ({
    page,
    request,
  }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/topology');
    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();

    const unique = randomUUID();
    const name = `Refresh Source ${unique}`;
    const registration = await request.post(`${apiBaseUrl}/api/sensors`, {
      data: {
        name,
        deviceIdentifier: `topology-refresh-${unique}`,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: {
          facility: 'Topology Test Facility',
          zone: 'Refresh Zone',
          nodeId: `refresh-${unique}`,
        },
        category: 'Environmental',
        telemetryDataType: 'Float',
      },
    });
    expect(registration.status()).toBe(201);

    await page.getByRole('button', { name: 'Refresh topology' }).click();
    await expect(page.getByRole('option', { name: new RegExp(name) })).toHaveCount(1);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);
  });

  test('does not display mismatched graph revisions during a simulated race', async ({ page }) => {
    // Fault-inject only the MST version. All other HTTP traffic reaches the
    // real API; mixed-revision results must never be published to the view.
    await page.route('**/api/topology/mst', async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as { graphVersion: number };
      await route.fulfill({
        response,
        json: { ...body, graphVersion: body.graphVersion + 1 },
      });
    });

    await page.goto('/topology');
    await expect(page.getByRole('alert')).toContainText(
      'The mesh changed during the topology request.',
    );
    await expect(page.locator('.topology-bootstrap__stats')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toBeVisible();
  });

  test('shows a recoverable error without inventing a graph on network failure', async ({
    page,
  }) => {
    // A deliberately failed connection tests UI error handling, not topology
    // correctness. Successful topology tests above use the real backend.
    await page.route('**/api/topology', (route) => route.abort('failed'));

    await page.goto('/topology');
    await expect(page.getByRole('alert')).toContainText(
      'The current topology could not be loaded.',
    );
    await expect(page.locator('.topology-bootstrap__stats')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toBeVisible();
  });
});
