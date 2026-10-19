import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** Register a real isolated device through ASP.NET Core, never a mocked UI row. */
async function registerDevice(
  request: APIRequestContext,
  category: 'Actuator' | 'Environmental',
  telemetryDataType: 'Boolean' | 'Integer',
): Promise<SensorResponse> {
  const suffix = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P2-07 Live ${category} ${suffix}`,
      deviceIdentifier: `p207-live-${suffix}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Realtime Integration Lab',
        zone: 'Browser Tests',
        nodeId: `Node-${suffix}`,
      },
      category,
      telemetryDataType,
    },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

async function bringOnline(request: APIRequestContext, sensorId: string): Promise<void> {
  const response = await request.patch(`${apiBaseUrl}/api/sensors/${sensorId}/status`, {
    data: { status: 'Online' },
  });
  expect(response.status()).toBe(200);
}

/** Submit genuine typed telemetry through the original Part 1 ingestion API. */
async function ingestInteger(
  request: APIRequestContext,
  sensorId: string,
  value: number,
  capturedAtUtc: string,
): Promise<void> {
  const response = await request.post(`${apiBaseUrl}/api/sensors/${sensorId}/telemetry`, {
    data: {
      capturedAtUtc,
      floatValue: null,
      integerValue: value,
      booleanValue: null,
    },
  });
  expect(response.status()).toBe(201);
}

test.describe('P2-07 live HTTP and SignalR workflow', () => {
  test('a SignalR invalidation brings a newly registered device into the console', async ({
    page,
    request,
  }) => {
    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByText('SignalR connected')).toBeVisible();

    // Register only after the hub is connected, to test actual invalidation.
    const device = await registerDevice(request, 'Environmental', 'Integer');
    const select = page.getByRole('combobox', { name: 'Selected device' });

    await expect(select.locator(`option[value="${device.id}"]`)).toHaveCount(1, {
      timeout: 15_000,
    });
    await select.selectOption(device.id);
    await expect(page.getByRole('heading', { name: device.name })).toBeVisible();
  });

  test('real RelayOn, accepted no-op and Stack Undo refresh commanded state and history', async ({
    page,
    request,
  }) => {
    const device = await registerDevice(request, 'Actuator', 'Boolean');
    await bringOnline(request, device.id);

    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await page.getByRole('combobox', { name: 'Selected device' }).selectOption(device.id);

    const relayOn = page.getByRole('button', { name: 'Relay On' });
    const undo = page.getByRole('button', { name: 'Undo latest command' });
    const state = page.getByTestId('realtime-commanded-state');
    const depth = page.getByTestId('realtime-undo-depth');
    const timeline = page.getByRole('table', { name: 'Chronological device history' });

    await expect(relayOn).toBeEnabled();
    await expect(state).toHaveText('Off');
    await expect(depth).toHaveText('0');
    await expect(undo).toBeDisabled();

    await relayOn.click();
    await expect(state).toHaveText('On');
    await expect(depth).toHaveText('1');
    await expect(undo).toBeEnabled();
    await expect(timeline).toContainText('Relay command');

    // A duplicate RelayOn is accepted and audited but cannot push the Stack.
    await relayOn.click();
    await expect(page.locator('.realtime-confirmation')).toContainText('no-op');
    await expect(depth).toHaveText('1');
    await expect(timeline).toContainText('Accepted no-op');

    await undo.click();
    await expect(state).toHaveText('Off');
    await expect(depth).toHaveText('0');
    await expect(undo).toBeDisabled();
    await expect(timeline).toContainText('Undo reversal');

    // Confirm through an independent HTTP request that the browser did not
    // merely swap its own labels; the server audit contains three real rows.
    const audit = await request.get(`${apiBaseUrl}/api/realtime/devices/${device.id}/commands`);
    expect(audit.status()).toBe(200);
    const result = (await audit.json()) as {
      totalAuditCount: number;
      actuator: { commandedState: string; undoDepth: number };
    };
    expect(result.totalAuditCount).toBe(3);
    expect(result.actuator.commandedState).toBe('Off');
    expect(result.actuator.undoDepth).toBe(0);
  });

  test('worker completion automatically updates dispatches, history and active conditions', async ({
    page,
    request,
  }) => {
    const device = await registerDevice(request, 'Environmental', 'Integer');

    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByText('SignalR connected')).toBeVisible();
    await page.getByRole('combobox', { name: 'Selected device' }).selectOption(device.id);

    const captured = Date.now() - 90_000;
    await ingestInteger(request, device.id, 10, new Date(captured).toISOString());
    await ingestInteger(request, device.id, 100, new Date(captured + 10_000).toISOString());

    // The queue and history GETs are invalidated by the actual worker, not
    // changed manually in React when requests are merely accepted.
    await expect(page.getByRole('table', { name: 'Recent worker dispatches' })).toContainText(
      'Integer 100',
      { timeout: 15_000 },
    );
    await expect(page.getByRole('table', { name: 'Chronological device history' })).toContainText(
      'Integer 100',
      { timeout: 15_000 },
    );
    await expect(
      page.locator('.realtime-condition').filter({ hasText: 'TelemetrySpike' }),
    ).toContainText('Critical', { timeout: 15_000 });

    const history = await request.get(
      `${apiBaseUrl}/api/realtime/devices/${device.id}/history?limit=10`,
    );
    expect(history.status()).toBe(200);
    const result = (await history.json()) as { totalRecordedEvents: number };
    expect(result.totalRecordedEvents).toBe(2);
  });
});
