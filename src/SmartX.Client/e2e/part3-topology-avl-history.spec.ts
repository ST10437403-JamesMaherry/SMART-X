import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse, TelemetryDataType } from '../src/api/sensorsApi';

const API_BASE = 'http://127.0.0.1:5095';
const START_UTC = '2026-11-10T08:00:00.000Z';
const END_UTC = '2026-11-10T09:04:00.000Z';

/** Each test registers a new sensor in the real isolated ASP.NET test server. */
async function registerHistorySensor(
  request: APIRequestContext,
  telemetryDataType: TelemetryDataType,
): Promise<SensorResponse> {
  const id = randomUUID();
  const response = await request.post(`${API_BASE}/api/sensors`, {
    data: {
      name: `AVL History ${telemetryDataType} ${id}`,
      deviceIdentifier: `avl-history-${id}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-09 Test Facility',
        zone: 'Indexed History',
        nodeId: `node-${id}`,
      },
      category: 'Environmental',
      telemetryDataType,
    },
  });

  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

/**
 * Fill native local time inputs with the local equivalents of exact UTC
 * instants. CI and developer machines may run in different timezones.
 */
async function selectUtcBounds(page: Page, fromIso: string, toIso: string): Promise<void> {
  const [fromLocal, toLocal] = await page.evaluate(
    ([from, to]) =>
      [from, to].map((iso) => {
        const date = new Date(iso);
        return new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
          .toISOString()
          .slice(0, 16);
      }),
    [fromIso, toIso],
  );

  await page.getByLabel('From (local time)').fill(fromLocal);
  await page.getByLabel('To (local time)').fill(toLocal);
}

async function openSensorHistory(page: Page, sensor: SensorResponse): Promise<void> {
  await page.goto('/topology');
  const selector = page.getByLabel('Select a registered sensor');
  await expect(selector.locator(`option[value="${sensor.id}"]`)).toHaveCount(1);
  await selector.selectOption(sensor.id);
  await expect(page.getByRole('heading', { name: 'Device history' })).toBeVisible();
  await expect(page.locator('.topology-history__identity')).toContainText(sensor.id);
}

test.describe('P3-09 AVL-backed per-device history foundation', () => {
  test('pages out-of-order and duplicate timestamps using the real AVL cursor', async ({
    page,
    request,
  }) => {
    const sensor = await registerHistorySensor(request, 'Integer');
    const ingestion = await request.post(`${API_BASE}/api/sensors/${sensor.id}/telemetry/batches`, {
      data: {
        batches: [
          [
            { capturedAtUtc: END_UTC, floatValue: null, integerValue: 40, booleanValue: null },
            { capturedAtUtc: START_UTC, floatValue: null, integerValue: 10, booleanValue: null },
            { capturedAtUtc: START_UTC, floatValue: null, integerValue: 20, booleanValue: null },
            {
              capturedAtUtc: '2026-11-10T08:01:00.000Z',
              floatValue: null,
              integerValue: 30,
              booleanValue: null,
            },
          ],
        ],
      },
    });
    expect(ingestion.status()).toBe(201);

    const historyRequests: string[] = [];
    page.on('request', (req) => {
      if (req.url().includes(`/api/sensors/${sensor.id}/telemetry/range?`)) {
        historyRequests.push(req.url());
      }
    });

    await openSensorHistory(page, sensor);
    await selectUtcBounds(page, START_UTC, END_UTC);
    await page.getByLabel('Readings per page').selectOption('2');
    await page.getByRole('button', { name: 'Query AVL history' }).click();

    const values = page.locator('.topology-history__value');
    await expect(values).toHaveText(['10', '20']);
    await expect(page.locator('.topology-history__page-heading')).toContainText('Page 1');
    await expect(page.locator('.topology-history__page-heading')).toContainText('Additional');

    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(values).toHaveText(['30', '40']);
    await expect(page.locator('.topology-history__page-heading')).toContainText('Page 2');
    await expect(page.getByRole('button', { name: 'Next page' })).toBeDisabled();

    // The next page must carry the server's composite cursor. No old recent
    // history endpoint can satisfy this assertion or preserve duplicates.
    expect(historyRequests.length).toBeGreaterThanOrEqual(2);
    const first = new URL(historyRequests[0]);
    const second = new URL(historyRequests[1]);
    expect(first.pathname).toContain('/telemetry/range');
    expect(first.searchParams.get('limit')).toBe('2');
    expect(first.searchParams.has('afterUtc')).toBe(false);
    expect(second.searchParams.get('afterUtc')).not.toBeNull();
    expect(second.searchParams.get('afterSequence')).not.toBeNull();

    await page.getByRole('button', { name: 'Previous page' }).click();
    await expect(values).toHaveText(['10', '20']);

    // Refreshing the graph must not unmount this independent history query.
    await page.getByRole('button', { name: 'Refresh topology' }).click();
    await expect(values).toHaveText(['10', '20']);
    await expect(page.getByLabel('Readings per page')).toHaveValue('2');
  });

  test('preserves Boolean false and explains a genuinely empty indexed range', async ({
    page,
    request,
  }) => {
    const sensor = await registerHistorySensor(request, 'Boolean');
    const ingestion = await request.post(`${API_BASE}/api/sensors/${sensor.id}/telemetry`, {
      data: {
        capturedAtUtc: START_UTC,
        floatValue: null,
        integerValue: null,
        booleanValue: false,
      },
    });
    expect(ingestion.status()).toBe(201);

    await openSensorHistory(page, sensor);
    await selectUtcBounds(page, START_UTC, END_UTC);
    await page.getByRole('button', { name: 'Query AVL history' }).click();

    await expect(page.locator('.topology-history__value')).toHaveText('False');
    await expect(page.locator('.topology-history__table tbody tr')).toHaveCount(1);
    await expect(page.locator('.topology-history__table')).toContainText('Boolean');

    await selectUtcBounds(page, '2026-11-12T08:00:00Z', '2026-11-12T09:00:00Z');
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.getByText(/No accepted telemetry was captured/)).toBeVisible();
    await expect(page.locator('.topology-history__table tbody tr')).toHaveCount(0);
  });

  test('rejects a reversed local range before an HTTP request and fits a 375px viewport', async ({
    page,
    request,
  }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    const sensor = await registerHistorySensor(request, 'Float');
    await openSensorHistory(page, sensor);

    let invalidNetworkQueries = 0;
    page.on('request', (req) => {
      if (req.url().includes(`/api/sensors/${sensor.id}/telemetry/range?`)) {
        invalidNetworkQueries++;
      }
    });

    await selectUtcBounds(page, END_UTC, START_UTC);
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__notice--error')).toContainText(
      'Range end must be on or after range start.',
    );
    expect(invalidNetworkQueries).toBe(0);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);
  });
});
