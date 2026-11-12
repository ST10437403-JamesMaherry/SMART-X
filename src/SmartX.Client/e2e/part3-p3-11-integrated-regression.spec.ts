import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

/**
 * P3-11 final browser regression.
 *
 * This suite intentionally uses the real ASP.NET Core API and the real React
 * application started by playwright.config.ts. It does not mock Smart-X state.
 * The purpose is to prove that the three completed architectural pillars work
 * as one application after the Part 3 topology work has been integrated.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';
const topologyEndpoint = `${apiBaseUrl}/api/topology`;

const pillars = [
  {
    path: '/ingestion',
    name: /Sensor Data Ingestion and Telemetry/,
    heading: /Sensor Data\s*Ingestion/,
  },
  {
    path: '/realtime',
    name: /Real-Time Command Stream and History/,
    heading: /Real-Time\s*Command Stream/,
  },
  {
    path: '/topology',
    name: /Network Topology and Mesh Routing/,
    heading: /Network Topology/,
  },
] as const;

function moduleNavigation(page: Page) {
  return page.getByRole('navigation', { name: 'Smart-X architectural modules' });
}

/**
 * Register through the production HTTP contract so the test proves that state
 * retained by the React provider still refers to a server-authoritative device.
 */
async function registerSensor(request: APIRequestContext): Promise<SensorResponse> {
  const uniqueId = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P3-11 Integrated Sensor ${uniqueId}`,
      deviceIdentifier: `p3-11-${uniqueId}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Final Regression Facility',
        zone: 'Integrated Workflow',
        nodeId: 'P3-11-Node',
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });

  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

test.describe('P3-11 integrated three-pillar regression', () => {
  test('all three pillars retain one document and the selected server device', async ({
    page,
    request,
  }) => {
    const sensor = await registerSensor(request);

    await page.goto('/ingestion');
    const navigation = moduleNavigation(page);
    await expect(navigation.getByRole('link')).toHaveCount(3);

    const sensorButton = page.getByRole('button', { name: new RegExp(sensor.name) });
    await sensorButton.click();
    await expect(sensorButton).toHaveAttribute('aria-pressed', 'true');

    // Safe text and numeric drafts are application state. They should survive
    // route unmounts; server-owned records remain authoritative over HTTP.
    const registrationName = page.locator('form.sensor-registration-form [name="name"]');
    const numericValue = page.locator('form.telemetry-form [name="numericValue"]');
    const draft = `P3-11 retained draft ${randomUUID()}`;
    await registrationName.fill(draft);
    await numericValue.fill('42.75');

    const documentStart = await page.evaluate(() => performance.timeOrigin);

    await navigation.getByRole('link', { name: pillars[1].name }).click();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(page.getByRole('heading', { level: 1 })).toContainText(pillars[1].heading);
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(sensor.id);

    await moduleNavigation(page).getByRole('link', { name: pillars[2].name }).click();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByRole('heading', { name: 'Live mesh topology' })).toBeVisible();
    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();

    await moduleNavigation(page).getByRole('link', { name: pillars[0].name }).click();
    await expect(page).toHaveURL(/\/ingestion$/);
    await expect(sensorButton).toHaveAttribute('aria-pressed', 'true');
    await expect(registrationName).toHaveValue(draft);
    await expect(numericValue).toHaveValue('42.75');

    // React Router must keep the same browser document and app-level provider.
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);

    const persisted = await request.get(`${apiBaseUrl}/api/sensors/${sensor.id}`);
    expect(persisted.ok()).toBeTruthy();
    expect((await persisted.json()) as SensorResponse).toMatchObject({
      id: sensor.id,
      name: sensor.name,
      deviceIdentifier: sensor.deviceIdentifier,
    });
  });

  test('topology read failure recovers in place without losing cross-pillar state', async ({
    page,
    request,
  }) => {
    const sensor = await registerSensor(request);

    await page.goto('/ingestion');
    await page.getByRole('button', { name: new RegExp(sensor.name) }).click();

    await page.route(topologyEndpoint, async (route) => {
      await route.abort('failed');
    });

    await moduleNavigation(page).getByRole('link', { name: pillars[2].name }).click();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByRole('alert')).toContainText('The current topology could not be loaded.');
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toBeVisible();

    const documentStart = await page.evaluate(() => performance.timeOrigin);
    await page.unroute(topologyEndpoint);
    await page.getByRole('button', { name: 'Retry topology request' }).click();

    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toHaveCount(0);
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);

    // The retry refreshes authoritative topology only; it must not clear the
    // application-wide selected device shared with Part 1 and Part 2.
    await moduleNavigation(page).getByRole('link', { name: pillars[1].name }).click();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(sensor.id);
  });

  test('keyboard navigation and narrow layout remain accessible across all pillars', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await page.goto('/');

    const documentStart = await page.evaluate(() => performance.timeOrigin);

    for (const pillar of pillars) {
      const link = moduleNavigation(page).getByRole('link', { name: pillar.name });
      await expect(link).toBeVisible();
      await link.focus();
      await expect(link).toBeFocused();
      await page.keyboard.press('Enter');

      await expect(page).toHaveURL(new RegExp(`${pillar.path}$`));
      await expect(
        moduleNavigation(page).getByRole('link', { name: pillar.name }),
      ).toHaveAttribute('aria-current', 'page');
      await expect(page.getByRole('heading', { level: 1 })).toContainText(pillar.heading);

      const horizontalOverflow = await page.evaluate(() => {
        const root = document.documentElement;
        return root.scrollWidth - root.clientWidth;
      });
      expect(horizontalOverflow, `horizontal overflow on ${pillar.path}`).toBeLessThanOrEqual(1);
    }

    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });
});
