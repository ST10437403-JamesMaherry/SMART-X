import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** Register real backend devices; no mocked records or browser-local stores. */
async function registerSensor(request: APIRequestContext, label: string): Promise<SensorResponse> {
  const suffix = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P2-07 ${label} ${suffix}`,
      deviceIdentifier: `p207-${label}-${suffix}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Operations Lab',
        zone: 'Realtime Evidence',
        nodeId: `Node-${label}`,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });

  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

test.describe('P2-07 real-time foundation', () => {
  test('loads real server diagnostics and retains selected device across pillars', async ({
    page,
    request,
  }) => {
    const first = await registerSensor(request, 'Alpha');
    const second = await registerSensor(request, 'Beta');

    await page.goto('/realtime');
    await expect(page.getByRole('heading', { name: 'Operations console' })).toBeVisible();
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByText('Accepted packets')).toBeVisible();
    await expect(page.getByText('Critical priority pending')).toBeVisible();

    const deviceSelect = page.getByRole('combobox', { name: 'Selected device' });
    await expect(deviceSelect.locator(`option[value="${first.id}"]`)).toHaveCount(1);
    await deviceSelect.selectOption(second.id);
    await expect(deviceSelect).toHaveValue(second.id);
    await expect(page.getByRole('heading', { name: second.name })).toBeVisible();

    // Navigation must not recreate the app-lifetime workspace state.
    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();

    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(second.id);
    await expect(page.getByRole('heading', { name: second.name })).toBeVisible();
  });

  test('manual refresh retrieves a newly registered server device', async ({ page, request }) => {
    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();

    const newDevice = await registerSensor(request, 'LateArrival');
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();

    const select = page.getByRole('combobox', { name: 'Selected device' });
    await expect(select.locator(`option[value="${newDevice.id}"]`)).toHaveCount(1);
    await select.selectOption(newDevice.id);
    await expect(page.getByRole('heading', { name: newDevice.name })).toBeVisible();
  });

  test('failed initial HTTP request has a usable retry, not a fabricated snapshot', async ({
    page,
  }) => {
    await page.route('**/api/realtime/snapshot', (route) => route.abort('failed'));
    await page.goto('/realtime');

    await expect(page.getByRole('alert')).toContainText('Unable to load');
    await expect(page.getByText('HTTP snapshot ready')).toHaveCount(0);

    await page.unroute('**/api/realtime/snapshot');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();

    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Dispatch lanes' })).toBeVisible();
  });
});
