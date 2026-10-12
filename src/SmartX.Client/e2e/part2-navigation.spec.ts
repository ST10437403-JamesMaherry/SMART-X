import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/**
 * Use real isolated API records so retention is checked against authoritative
 * sensor data. UUID identifiers remain unique across runs and retries.
 */
async function createSensor(request: APIRequestContext, label: string): Promise<SensorResponse> {
  const suffix = randomUUID();

  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `Navigation ${label} ${suffix}`,
      deviceIdentifier: `nav-${label}-${suffix}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Navigation Facility',
        zone: 'Retention Zone',
        nodeId: `Node-${label}`,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });

  expect(response.status()).toBe(201);

  return (await response.json()) as SensorResponse;
}

function sensorButton(page: Page, sensor: SensorResponse) {
  return page.getByRole('button', { name: new RegExp(sensor.name) });
}

test.describe('Part 2 navigation foundation', () => {
  let firstSensor: SensorResponse;
  let secondSensor: SensorResponse;

  // Pages receive separate browser contexts. Only these stable server records
  // are shared, keeping the Part 1 dashboard regression free of fixture churn.
  test.beforeAll(async ({ request }) => {
    firstSensor = await createSensor(request, 'A');
    secondSensor = await createSensor(request, 'B');
  });

  test('opens both pillars without reloading and marks the active link', async ({ page }) => {
    await page.goto('/ingestion');

    const ingestionLink = page.getByRole('link', {
      name: /Sensor Data Ingestion and Telemetry/,
    });

    const realtimeLink = page.getByRole('link', {
      name: /Real-Time Command Stream and History/,
    });

    await expect(ingestionLink).toHaveAttribute('aria-current', 'page');

    const documentStart = await page.evaluate(() => performance.timeOrigin);

    // Native links must work with the keyboard as well as a pointer.
    await realtimeLink.focus();
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(/\/realtime$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Real-Time\s*Command Stream/);
    await expect(realtimeLink).toHaveAttribute('aria-current', 'page');
    await expect(ingestionLink).not.toHaveAttribute('aria-current', 'page');
    await expect(page.getByRole('main')).toHaveCount(1);

    const topology = page.getByRole('button', { name: /Network Topology and Mesh Routing/ });

    await expect(topology).toHaveAttribute('aria-disabled', 'true');
    await topology.focus();
    await expect(topology).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/realtime$/);

    await ingestionLink.click();

    await expect(page).toHaveURL(/\/ingestion$/);
    await expect(ingestionLink).toHaveAttribute('aria-current', 'page');
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });

  test('retains selection and safe drafts across pillars without sharing device inputs', async ({
    page,
  }) => {
    await page.goto('/ingestion');

    const firstSensorButton = sensorButton(page, firstSensor);
    const secondSensorButton = sensorButton(page, secondSensor);

    await firstSensorButton.click();
    await expect(firstSensorButton).toHaveAttribute('aria-pressed', 'true');

    const registration = page.locator('form.sensor-registration-form');
    const numericValue = page.locator('form.telemetry-form [name="numericValue"]');
    const batchForm = page.locator('form.telemetry-batch-form');

    await registration.locator('[name="name"]').fill('Unsubmitted registration draft');
    await registration.locator('[name="facility"]').fill('Draft Facility');
    await numericValue.fill('42.5');
    await batchForm.getByLabel('Batch 01', { exact: true }).fill('1.5, 2.5');
    await batchForm.getByRole('button', { name: 'Add batch', exact: true }).click();
    await batchForm.getByLabel('Batch 02', { exact: true }).fill('3.5');

    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await expect(page).toHaveURL(/\/realtime$/);

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();

    await expect(firstSensorButton).toHaveAttribute('aria-pressed', 'true');
    await expect(registration.locator('[name="name"]')).toHaveValue(
      'Unsubmitted registration draft',
    );
    await expect(registration.locator('[name="facility"]')).toHaveValue('Draft Facility');
    await expect(numericValue).toHaveValue('42.5');
    await expect(batchForm.getByLabel('Batch 01', { exact: true })).toHaveValue('1.5, 2.5');
    await expect(batchForm.getByLabel('Batch 02', { exact: true })).toHaveValue('3.5');

    await secondSensorButton.click();

    await expect(secondSensorButton).toHaveAttribute('aria-pressed', 'true');
    await expect(numericValue).toHaveValue('');
    await expect(batchForm.getByLabel('Batch 01', { exact: true })).toHaveValue('');
    await expect(batchForm.getByLabel('Batch 02', { exact: true })).toHaveCount(0);

    await numericValue.fill('77.5');
    await firstSensorButton.click();

    await expect(firstSensorButton).toHaveAttribute('aria-pressed', 'true');
    await expect(numericValue).toHaveValue('42.5');
    await expect(batchForm.getByLabel('Batch 02', { exact: true })).toHaveValue('3.5');
  });

  test('loads direct pillar URLs and recovers unknown routes', async ({ page }, testInfo) => {
    for (const route of ['/', '/ingestion', '/realtime']) {
      await page.goto(route);

      await expect(page.getByRole('main')).toHaveCount(1);
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expect(
        page.getByRole('button', { name: /Network Topology and Mesh Routing/ }),
      ).toHaveAttribute('aria-disabled', 'true');

      const activePillar = page.locator('.pillar-control[aria-current="page"]');

      if (route === '/') {
        await expect(activePillar).toHaveCount(0);
      } else {
        await expect(activePillar).toHaveCount(1);
        await expect(activePillar).toHaveAttribute('href', route);
      }
    }

    // A hard refresh must also resolve the new client route successfully.
    await page.reload();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Real-Time\s*Command Stream/);

    // Capture real navigation evidence from the browser run, not a mockup.
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.screenshot({
      path: testInfo.outputPath('01-part2-navigation.png'),
      fullPage: true,
    });

    // Exercise the new page at the same widths as the Part 1 regression.
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });

      const overflow = await page.evaluate(() => {
        const root = document.documentElement;

        return root.scrollWidth - root.clientWidth;
      });

      expect(overflow).toBeLessThanOrEqual(1);
    }

    await page.goto('/unknown-smartx-pillar');
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('link', { name: 'Smart-X home', exact: true })).toBeVisible();
    await expect(page.getByRole('main')).toHaveCount(1);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/IoT Mesh\s*Ecosystem/);
  });

  test('preserves selection and drafts through browser Back and Forward', async ({ page }) => {
    await page.goto('/ingestion');
    await sensorButton(page, secondSensor).click();

    const registrationName = page.locator('form.sensor-registration-form [name="name"]');
    const numericValue = page.locator('form.telemetry-form [name="numericValue"]');

    await registrationName.fill('Browser history draft');
    await numericValue.fill('55.25');

    const documentStart = await page.evaluate(() => performance.timeOrigin);

    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await expect(page).toHaveURL(/\/realtime$/);
    await page.getByRole('link', { name: 'Smart-X home', exact: true }).click();
    await expect(page).toHaveURL(/\/$/);

    await page.goBack();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(
      page.getByRole('link', { name: /Real-Time Command Stream and History/ }),
    ).toHaveAttribute('aria-current', 'page');

    await page.goBack();
    await expect(page).toHaveURL(/\/ingestion$/);
    await expect(sensorButton(page, secondSensor)).toHaveAttribute('aria-pressed', 'true');
    await expect(registrationName).toHaveValue('Browser history draft');
    await expect(numericValue).toHaveValue('55.25');

    await page.goForward();
    await expect(page).toHaveURL(/\/realtime$/);
    await page.goForward();
    await expect(page).toHaveURL(/\/$/);

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await expect(sensorButton(page, secondSensor)).toHaveAttribute('aria-pressed', 'true');
    await expect(registrationName).toHaveValue('Browser history draft');
    await expect(numericValue).toHaveValue('55.25');
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });

  test('clears pending attachment files when changing device or pillar', async ({ page }) => {
    await page.goto('/ingestion');
    await sensorButton(page, firstSensor).click();

    const fileInput = page.locator('#attachment-file');
    const selectedFile = page.locator('.attachment-selection');
    const pendingFile = {
      name: 'navigation-config.json',
      mimeType: 'application/json',
      buffer: Buffer.from('{"sampleRateSeconds":5,"enabled":true}'),
    };

    await fileInput.setInputFiles(pendingFile);
    await expect(selectedFile).toContainText(pendingFile.name);

    await sensorButton(page, secondSensor).click();
    await expect(sensorButton(page, secondSensor)).toHaveAttribute('aria-pressed', 'true');
    await expect(fileInput).toHaveValue('');
    await expect(selectedFile).toHaveCount(0);

    await sensorButton(page, firstSensor).click();
    await expect(fileInput).toHaveValue('');
    await expect(selectedFile).toHaveCount(0);

    await fileInput.setInputFiles(pendingFile);
    await expect(selectedFile).toContainText(pendingFile.name);
    await page.locator('form.telemetry-form [name="numericValue"]').fill('11.5');

    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(fileInput).toHaveCount(0);

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await expect(sensorButton(page, firstSensor)).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('form.telemetry-form [name="numericValue"]')).toHaveValue('11.5');
    await expect(fileInput).toHaveValue('');
    await expect(selectedFile).toHaveCount(0);
  });

  test('retains the selected device and drafts while a registry load fails and recovers', async ({
    page,
  }) => {
    await page.goto('/ingestion');

    // B is not the default first-created sensor, so clearing its retained ID
    // during a failed load would be exposed when the retry falls back to A.
    await sensorButton(page, secondSensor).click();

    const registrationName = page.locator('form.sensor-registration-form [name="name"]');
    const numericValue = page.locator('form.telemetry-form [name="numericValue"]');

    await registrationName.fill('Recovery draft');
    await numericValue.fill('63.5');

    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await expect(page).toHaveURL(/\/realtime$/);

    const registryUrl = `${apiBaseUrl}/api/sensors`;

    // Fail only the browser registry read; the actual API keeps running.
    await page.route(registryUrl, async (route) => {
      if (route.request().method() === 'GET') {
        await route.abort('failed');
        return;
      }

      await route.continue();
    });

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();

    const registry = page.locator('.sensor-registry');

    await expect(registry.getByRole('alert')).toBeVisible();
    await expect(registrationName).toHaveValue('Recovery draft');
    await expect(numericValue).toHaveCount(0);
    await expect(page.locator('#attachment-file')).toHaveCount(0);

    await page.unroute(registryUrl);
    await registry.getByRole('button', { name: 'Try again', exact: true }).click();

    await expect(sensorButton(page, secondSensor)).toHaveAttribute('aria-pressed', 'true');
    await expect(registrationName).toHaveValue('Recovery draft');
    await expect(numericValue).toHaveValue('63.5');
    await expect(registry.getByRole('alert')).toHaveCount(0);
    await expect(page.locator('#attachment-file')).toHaveCount(1);
  });
});
