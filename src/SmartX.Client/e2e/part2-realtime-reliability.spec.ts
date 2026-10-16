import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page, type Route } from '@playwright/test';

/**
 * P2-07 Commit 4: failure recovery, isolated device state and finite-run safety.
 *
 * Playwright starts the actual ASP.NET Core application and Vite. Tests may
 * interrupt the browser's requests, but they NEVER fabricate successful API
 * payloads, write into Domain collections or manipulate local device state.
 *
 * Each created sensor has a unique identifier because all Playwright tests
 * share one process-local server during a single test run.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';

type SensorKind =
  | { category: 'Environmental'; telemetryDataType: 'Integer' }
  | { category: 'Actuator'; telemetryDataType: 'Boolean' };

interface RegisteredDevice {
  readonly id: string;
  readonly name: string;
}

async function registerDevice(
  request: APIRequestContext,
  label: string,
  kind: SensorKind = { category: 'Environmental', telemetryDataType: 'Integer' },
): Promise<RegisteredDevice> {
  const identifier = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P2-07 Reliability ${label} ${identifier.slice(0, 8)}`,
      deviceIdentifier: `p207-reliability-${identifier}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Part 2 Test Facility',
        zone: 'Browser Reliability',
        nodeId: `Node-${identifier.slice(0, 8)}`,
      },
      category: kind.category,
      telemetryDataType: kind.telemetryDataType,
    },
  });

  expect(response.status()).toBe(201);
  const result = (await response.json()) as RegisteredDevice;
  expect(result.id).toBeTruthy();
  return result;
}

async function changeStatus(
  request: APIRequestContext,
  sensorId: string,
  status: 'Online' | 'Offline',
): Promise<void> {
  const response = await request.patch(`${apiBaseUrl}/api/sensors/${sensorId}/status`, {
    data: { status },
  });
  expect(response.status()).toBe(200);
}

async function openConsole(page: Page): Promise<void> {
  await page.goto('/realtime');
  await expect(page.getByRole('heading', { name: 'Operations console' })).toBeVisible();
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
}

/** A selected device is matched by GUID, never by an ambiguous display name. */
async function selectDevice(page: Page, sensorId: string): Promise<void> {
  const selector = page.getByRole('combobox', { name: 'Selected device' });
  await expect(selector.locator(`option[value="${sensorId}"]`)).toHaveCount(1, {
    timeout: 15_000,
  });
  await selector.selectOption(sensorId);
  await expect(selector).toHaveValue(sensorId);
}

test.describe('P2-07 real-time reliability and finite-operation safety', () => {
  test('failed HTTP refresh retains selected device and last-known counters until recovery', async ({
    page,
    request,
  }) => {
    const first = await registerDevice(request, 'Existing');
    await openConsole(page);
    await selectDevice(page, first.id);
    await expect(page.getByRole('heading', { name: first.name })).toBeVisible();

    const deviceCount = page
      .locator('.realtime-metric')
      .filter({ hasText: 'Registered devices' })
      .locator('strong');
    const countBeforeFailure = await deviceCount.textContent();

    // Deliberately fail only the browser GET. The API and its genuine sensor
    // repository continue running; the last successful view must be retained.
    await page.route('**/api/realtime/snapshot', (route) => route.abort('failed'));
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();

    await expect(
      page.locator('.realtime-message--error').filter({
        hasText: 'Last successful server snapshot retained',
      }),
    ).toBeVisible();
    await expect(page.getByRole('heading', { name: first.name })).toBeVisible();
    await expect(deviceCount).toHaveText(countBeforeFailure ?? '');
    await expect(page.getByRole('heading', { name: 'Dispatch lanes' })).toBeVisible();

    await page.unroute('**/api/realtime/snapshot');
    const second = await registerDevice(request, 'AfterRecovery');

    // This test verifies an explicit operator retry. The separate reconnect
    // test proves automatic SignalR-triggered recovery after missed events.
    const retry = page.getByRole('button', { name: 'Refresh snapshot' });
    await expect(retry).toBeEnabled();
    await retry.click();
    await expect(
      page
        .getByRole('combobox', { name: 'Selected device' })
        .locator(`option[value="${second.id}"]`),
    ).toHaveCount(1, { timeout: 15_000 });
    await selectDevice(page, second.id);
    await expect(page.getByRole('heading', { name: second.name })).toBeVisible();
    await expect(page.locator('.realtime-message--error')).toHaveCount(0);
  });

  test('initial SignalR negotiation outage leaves HTTP usable and reconnect refreshes missed devices', async ({
    page,
    request,
  }) => {
    // Block only the new real-time hub's negotiation endpoint. This simulates
    // an actual transport failure without mocking HTTP device snapshots.
    const negotiateRoute = '**/hubs/realtime/negotiate**';
    await page.route(negotiateRoute, (route) => route.abort('failed'));

    await openConsole(page);
    await expect(page.getByText('SignalR disconnected')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('heading', { name: 'Dispatch lanes' })).toBeVisible();

    // Registration occurs while this browser is disconnected. The hub cannot
    // deliver its invalidation until reconnection causes a fresh HTTP read.
    const missed = await registerDevice(request, 'DuringOutage');
    await expect(
      page
        .getByRole('combobox', { name: 'Selected device' })
        .locator(`option[value="${missed.id}"]`),
    ).toHaveCount(0);

    await page.unroute(negotiateRoute);
    await expect(page.getByText('SignalR connected')).toBeVisible({ timeout: 20_000 });
    await selectDevice(page, missed.id);
    await expect(page.getByRole('heading', { name: missed.name })).toBeVisible();
  });

  test('device switching isolates actual chronological history from other sensors', async ({
    page,
    request,
  }) => {
    const first = await registerDevice(request, 'HistoryA');
    const second = await registerDevice(request, 'HistoryB');

    const ingestion = await request.post(`${apiBaseUrl}/api/sensors/${first.id}/telemetry`, {
      data: {
        capturedAtUtc: new Date(Date.now() - 60_000).toISOString(),
        floatValue: null,
        integerValue: 876543,
        booleanValue: null,
      },
    });
    expect(ingestion.status()).toBe(201);

    // The SortedDictionary is populated by the REAL worker after dispatch;
    // request acceptance alone is not sufficient for this assertion.
    await expect
      .poll(
        async () => {
          const response = await request.get(
            `${apiBaseUrl}/api/realtime/devices/${first.id}/history?limit=10`,
          );
          if (!response.ok()) return 0;
          const history = (await response.json()) as {
            events: { telemetry: { integerValue: number | null } | null }[];
          };
          return history.events.filter((event) => event.telemetry?.integerValue === 876543).length;
        },
        { timeout: 15_000, message: 'Waiting for one genuine worker-history event' },
      )
      .toBe(1);

    await openConsole(page);
    await selectDevice(page, first.id);
    await expect(page.getByRole('table', { name: 'Chronological device history' })).toContainText(
      'Integer 876543',
    );

    await selectDevice(page, second.id);
    await expect(
      page.getByText('No successful processing or accepted commands for this device.'),
    ).toBeVisible();
    await expect(page.getByRole('table', { name: 'Chronological device history' })).toHaveCount(0);

    // Returning to the first device must refetch and display its own history.
    await selectDevice(page, first.id);
    await expect(page.getByRole('table', { name: 'Chronological device history' })).toContainText(
      'Integer 876543',
    );
  });

  test('relay controls track server Online and Offline transitions while preserving read-only audit', async ({
    page,
    request,
  }) => {
    const actuator = await registerDevice(request, 'RelayEligibility', {
      category: 'Actuator',
      telemetryDataType: 'Boolean',
    });
    await openConsole(page);
    await selectDevice(page, actuator.id);

    const relay = page.getByRole('button', { name: 'Relay On' });
    const undo = page.getByRole('button', { name: 'Undo latest command' });
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
    await expect(relay).toBeDisabled();
    await expect(undo).toBeDisabled();

    await changeStatus(request, actuator.id, 'Online');
    await expect(relay).toBeEnabled({ timeout: 15_000 });
    await expect(undo).toBeDisabled();

    await changeStatus(request, actuator.id, 'Offline');
    await expect(relay).toBeDisabled({ timeout: 15_000 });
    await expect(
      page.getByText('Commands are disabled while the registered sensor is Offline.'),
    ).toBeVisible();

    // The server's command-audit metadata is still readable when Offline.
    await expect(page.getByText('Accepted command audit')).toBeVisible();
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
  });

  test('a failed actuator audit GET exposes retry without hiding the operations console', async ({
    page,
    request,
  }) => {
    const actuator = await registerDevice(request, 'AuditRetry', {
      category: 'Actuator',
      telemetryDataType: 'Boolean',
    });
    await changeStatus(request, actuator.id, 'Online');

    const auditRoute = `**/api/realtime/devices/${actuator.id}/commands`;
    let blockedAuditReads = 0;
    const failAuditRead = async (route: Route): Promise<void> => {
      blockedAuditReads += 1;
      await route.abort('failed');
    };
    await page.route(auditRoute, failAuditRead);
    await openConsole(page);
    await selectDevice(page, actuator.id);

    const retryAudit = page.getByRole('button', { name: 'Retry command audit' });
    await expect(retryAudit).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Dispatch lanes' })).toBeVisible();

    // Click while failures are still enforced. Removing the failure route
    // first allows an automatic SignalR refresh to hide the retry button
    // mid-click, which made this test intermittent on GitHub Actions.
    const failuresBeforeRetry = blockedAuditReads;
    await retryAudit.click();
    await expect
      .poll(() => blockedAuditReads, {
        message: 'Manual Retry must issue another real command-audit GET',
      })
      .toBeGreaterThan(failuresBeforeRetry);
    await expect(retryAudit).toBeVisible();

    // Recover over the ordinary, permanently available manual refresh path.
    // If a background refresh also completes, it is legitimate recovery and
    // must not make this test depend on a disappearing error button.
    await page.unroute(auditRoute, failAuditRead);
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
    await expect(page.getByText('Accepted command audit').locator('..')).toContainText('0');
    await expect(retryAudit).toHaveCount(0);
  });

  test('invalid and fractional finite-load limits block submission until corrected', async ({
    page,
  }) => {
    await openConsole(page);
    const devices = page.getByRole('spinbutton', { name: 'Load devices' });
    const readings = page.getByRole('spinbutton', { name: 'Readings per device' });
    const run = page.getByRole('button', { name: 'Run finite load' });

    await expect(devices).toHaveValue('100');
    await expect(readings).toHaveValue('100');
    await expect(run).toBeEnabled();

    await devices.fill('0');
    await expect(run).toBeDisabled();
    await expect(page.getByText('Choose valid finite limits')).toBeVisible();

    await devices.fill('101');
    await expect(run).toBeDisabled();

    await devices.fill('1.5');
    await expect(run).toBeDisabled();

    await devices.fill('2');
    await readings.fill('101');
    await expect(run).toBeDisabled();

    await readings.fill('5');
    await expect(page.getByText('10 planned readings')).toBeVisible();
    await expect(run).toBeEnabled();
    await expect(page.locator('.realtime-simulator-result')).toHaveCount(0);
  });

  test('Stop aborts an in-flight finite load without inventing confirmed readings', async ({
    page,
    request,
  }) => {
    await openConsole(page);
    await page.getByRole('spinbutton', { name: 'Load devices' }).fill('3');
    await page.getByRole('spinbutton', { name: 'Readings per device' }).fill('5');

    // Hold the first actual batch POST at the browser network boundary.
    // We can therefore click Stop during an in-flight request without
    // introducing artificial server delays or submitting thousands of rows.
    let releaseBatch: (() => void) | undefined;
    let sawBatch: (() => void) | undefined;
    const releaseGate = new Promise<void>((resolve) => {
      releaseBatch = resolve;
    });
    const enteredGate = new Promise<void>((resolve) => {
      sawBatch = resolve;
    });
    const batchRoute = /\/api\/sensors\/[^/]+\/telemetry\/batches(?:\?.*)?$/;

    await page.route(batchRoute, async (route) => {
      if (route.request().method() !== 'POST') {
        await route.continue();
        return;
      }

      sawBatch?.();
      await releaseGate;
      try {
        // The browser may already have cancelled the fetch after Stop.
        await route.continue();
      } catch {
        // A cancelled request is expected, not a mocked successful batch.
      }
    });

    try {
      await page.getByRole('button', { name: 'Run finite load' }).click();
      await enteredGate;
      await expect(page.getByRole('button', { name: 'Stop active scenario' })).toBeVisible();
      await page.getByRole('button', { name: 'Stop active scenario' }).click();

      await expect(
        page.getByText(
          'Scenario stopped. Successfully accepted requests remain in Smart-X and may still be processing.',
        ),
      ).toBeVisible({ timeout: 15_000 });
      await expect(page.locator('.realtime-simulator-result')).toHaveCount(0);
      await expect(page.getByTestId('simulator-accepted')).toHaveText('0');
      await expect(page.getByRole('button', { name: 'Run finite load' })).toBeEnabled();

      // A Stop is a browser cancellation, never a global worker pause.
      const processing = await request.get(`${apiBaseUrl}/api/realtime/processing`);
      expect(processing.status()).toBe(200);
      const body = (await processing.json()) as { ordinaryDispatchPaused: boolean };
      expect(body.ordinaryDispatchPaused).toBe(false);
    } finally {
      releaseBatch?.();
      await page.unroute(batchRoute);
    }
  });
});
