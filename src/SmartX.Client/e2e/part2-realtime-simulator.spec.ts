import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { RealtimeActiveConditionsResponse } from '../src/api/realtimeApi';
import type { TelemetryReadingResponse } from '../src/api/telemetryApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** Real API data is used for assertions; there are no mocked response rows. */
async function readJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(`${apiBaseUrl}${path}`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as T;
}

async function openSimulator(page: Page): Promise<void> {
  await page.goto('/realtime');
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'IoT traffic simulator' })).toBeVisible();
}

async function waitForCompletion(page: Page): Promise<void> {
  await expect(page.locator('.realtime-simulator-result')).toBeVisible({ timeout: 30_000 });
  await expect(
    page.getByText('Scenario completed. All displayed confirmations came from the real API.'),
  ).toBeVisible();
}

/** The runner selects its first newly registered device using shared state. */
async function selectedDemoDevice(page: Page, prefix: string): Promise<string> {
  const select = page.getByRole('combobox', { name: 'Selected device' });
  await expect(select.locator('option:checked')).toContainText(prefix);
  return select.inputValue();
}

test.describe('P2-07 API-driven IoT simulator', () => {
  test('normal traffic creates typed readings and real worker history', async ({
    page,
    request,
  }) => {
    await openSimulator(page);
    await page.getByRole('button', { name: 'Run normal traffic' }).click();
    await waitForCompletion(page);

    await expect(page.getByTestId('simulator-devices')).toHaveText('1');
    await expect(page.getByTestId('simulator-accepted')).toHaveText('3');
    const sensorId = await selectedDemoDevice(page, 'P2-07 normal');

    const history = await readJson<{
      totalRecordedEvents: number;
      events: { kind: string; telemetry: TelemetryReadingResponse | null }[];
    }>(request, `/api/realtime/devices/${sensorId}/history?limit=10`);
    expect(history.totalRecordedEvents).toBe(3);
    expect(history.events.map((entry) => entry.telemetry?.floatValue)).toEqual([20, 20.1, 20.2]);
    expect(history.events.every((entry) => entry.kind === 'TelemetryProcessed')).toBeTruthy();
  });

  test('spike simulation verifies escalation, recovery and recurrence in the HashSet', async ({
    page,
    request,
  }) => {
    await openSimulator(page);
    await page.getByRole('button', { name: 'Run numeric spike' }).click();
    await waitForCompletion(page);
    await expect(page.getByTestId('simulator-accepted')).toHaveText('7');
    await expect(page.locator('.realtime-simulator-result')).toContainText(
      'Two consecutive stable readings removed both active memberships.',
    );

    const sensorId = await selectedDemoDevice(page, 'P2-07 spike');
    const active = await readJson<RealtimeActiveConditionsResponse>(
      request,
      '/api/realtime/snapshot/active-conditions',
    );
    const spike = active.activeConditions.filter(
      (issue) => issue.sensorProfileId === sensorId && issue.conditionType === 'TelemetrySpike',
    );
    expect(spike).toHaveLength(1);
    expect(spike[0].severity).toBe('Critical');
    expect(spike[0].occurrenceCount).toBe(1);

    const history = await readJson<{ totalRecordedEvents: number }>(
      request,
      `/api/realtime/devices/${sensorId}/history?limit=20`,
    );
    expect(history.totalRecordedEvents).toBe(7);
  });

  test('Boolean actuator scenario uses typed traffic and genuine command Undo audit', async ({
    page,
    request,
  }) => {
    await openSimulator(page);
    await page.getByRole('button', { name: 'Run boolean actuator' }).click();
    await waitForCompletion(page);
    await expect(page.getByTestId('simulator-accepted')).toHaveText('2');
    const sensorId = await selectedDemoDevice(page, 'P2-07 actuator');

    const audit = await readJson<{
      actuator: { commandedState: string; undoDepth: number };
      totalAuditCount: number;
    }>(request, `/api/realtime/devices/${sensorId}/commands`);
    expect(audit.totalAuditCount).toBe(3);
    expect(audit.actuator.commandedState).toBe('Off');
    expect(audit.actuator.undoDepth).toBe(0);

    const readings = await readJson<readonly TelemetryReadingResponse[]>(
      request,
      `/api/sensors/${sensorId}/telemetry?limit=10`,
    );
    expect(readings.map((reading) => reading.booleanValue)).toEqual([false, true]);
  });

  test('disconnect recovery returns Online and removes the active disconnect key', async ({
    page,
    request,
  }) => {
    await openSimulator(page);
    await page.getByRole('button', { name: 'Run disconnect / recover' }).click();
    await waitForCompletion(page);
    const sensorId = await selectedDemoDevice(page, 'P2-07 disconnect');

    const sensor = await readJson<{ status: string }>(request, `/api/sensors/${sensorId}`);
    expect(sensor.status).toBe('Online');

    const active = await readJson<RealtimeActiveConditionsResponse>(
      request,
      '/api/realtime/snapshot/active-conditions',
    );
    expect(
      active.activeConditions.filter(
        (issue) =>
          issue.sensorProfileId === sensorId && issue.conditionType === 'SensorDisconnected',
      ),
    ).toHaveLength(0);

    const history = await readJson<{
      issues: { sensorProfileId: string; type: string; state: string }[];
    }>(request, '/api/dashboard/issues?includeResolved=true');
    expect(history.issues).toContainEqual(
      expect.objectContaining({
        sensorProfileId: sensorId,
        type: 'SensorDisconnected',
        state: 'Resolved',
      }),
    );
  });

  test('priority bypass uses real ordinary pause and restores worker dequeue order', async ({
    page,
    request,
  }) => {
    await openSimulator(page);

    // Manual pause is an explicit, authoritative server control.
    await page.getByRole('button', { name: 'Pause ordinary dispatch' }).click();
    await expect(page.getByRole('button', { name: 'Resume ordinary dispatch' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Run critical bypass' })).toBeDisabled();
    await page.getByRole('button', { name: 'Resume ordinary dispatch' }).click();
    await expect(page.getByRole('button', { name: 'Run critical bypass' })).toBeEnabled();

    await page.getByRole('button', { name: 'Run critical bypass' }).click();
    await waitForCompletion(page);
    const sensorId = await selectedDemoDevice(page, 'P2-07 priority');

    const processing = await readJson<{
      ordinaryDispatchPaused: boolean;
      recentDispatches: {
        dequeueSequence: number;
        lane: string;
        outcome: string;
        telemetry: { sensorProfileId: string; integerValue: number | null };
      }[];
    }>(request, '/api/realtime/processing');

    expect(processing.ordinaryDispatchPaused).toBe(false);
    const dispatches = processing.recentDispatches
      .filter((entry) => entry.telemetry.sensorProfileId === sensorId)
      .sort((left, right) => left.dequeueSequence - right.dequeueSequence);
    expect(dispatches.map((entry) => entry.telemetry.integerValue)).toEqual([100, 10, 11]);
    expect(dispatches.map((entry) => entry.lane)).toEqual(['Critical', 'Ordinary', 'Ordinary']);
    expect(dispatches.every((entry) => entry.outcome === 'Processed')).toBeTruthy();
  });

  test('small finite load uses real bounded typed batches and reports exact receipts', async ({
    page,
    request,
  }) => {
    await openSimulator(page);
    await expect(page.getByRole('spinbutton', { name: 'Load devices' })).toHaveValue('100');
    await expect(page.getByRole('spinbutton', { name: 'Readings per device' })).toHaveValue('100');

    // Do not send 10,000 packets during every CI run. Exercise the identical
    // API pathway with a reproducible 2-device / 10-reading finite run.
    await page.getByRole('spinbutton', { name: 'Load devices' }).fill('2');
    await page.getByRole('spinbutton', { name: 'Readings per device' }).fill('5');
    await page.getByRole('button', { name: 'Run finite load' }).click();
    await waitForCompletion(page);

    await expect(page.getByTestId('simulator-devices')).toHaveText('2');
    await expect(page.getByTestId('simulator-accepted')).toHaveText('10');
    await expect(page.locator('.realtime-simulator-result')).toContainText(
      '10 typed readings confirmed by 2 actual batch API responses.',
    );

    const sensorId = await selectedDemoDevice(page, 'P2-07 load1');
    const recent = await readJson<readonly TelemetryReadingResponse[]>(
      request,
      `/api/sensors/${sensorId}/telemetry?limit=10`,
    );
    expect(recent).toHaveLength(5);
    expect(recent.every((reading) => reading.telemetryDataType === 'Float')).toBeTruthy();
  });
});
