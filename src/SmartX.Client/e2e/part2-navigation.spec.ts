import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

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

test.describe('Part 2 navigation foundation', () => {
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
    request,
  }) => {
    const firstSensor = await createSensor(request, 'A');
    const secondSensor = await createSensor(request, 'B');

    await page.goto('/ingestion');

    const firstSensorButton = page.getByRole('button', { name: new RegExp(firstSensor.name) });
    const secondSensorButton = page.getByRole('button', { name: new RegExp(secondSensor.name) });

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
});
