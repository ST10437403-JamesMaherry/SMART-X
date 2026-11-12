import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse, TelemetryDataType } from '../src/api/sensorsApi';
import type { TelemetryRangePageResponse } from '../src/api/telemetryRangeApi';

const API_BASE = 'http://127.0.0.1:5095';
const FIRST_UTC = '2026-11-09T09:00:00.000Z';
const DUPLICATE_UTC = '2026-11-09T09:01:00.000Z';
const THIRD_UTC = '2026-11-09T09:02:00.000Z';
const LAST_UTC = '2026-11-09T09:03:00.000Z';

type Packet = {
  readonly capturedAtUtc: string;
  readonly floatValue: number | null;
  readonly integerValue: number | null;
  readonly booleanValue: boolean | null;
};

/** Register a unique real API sensor; never rely on a pre-populated database. */
async function registerSensor(
  request: APIRequestContext,
  type: TelemetryDataType,
  options: { readonly mac?: boolean } = {},
): Promise<SensorResponse> {
  const uuid = randomUUID();
  const hex = uuid.replaceAll('-', '').toUpperCase();
  const mac = `02:${hex.slice(0, 2)}:${hex.slice(2, 4)}:${hex.slice(4, 6)}:${hex.slice(6, 8)}:${hex.slice(8, 10)}`;
  const response = await request.post(`${API_BASE}/api/sensors`, {
    data: {
      name: `P3-09 Final ${type} ${uuid}`,
      deviceIdentifier: options.mac ? mac : `p309-final-${uuid}`,
      identifierKind: options.mac ? 'MacAddress' : 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-09 API Verification',
        zone: 'Chronological Telemetry',
        nodeId: `p309-${uuid}`,
      },
      category: 'Environmental',
      telemetryDataType: type,
    },
  });
  expect(response.status(), await response.text()).toBe(201);
  return (await response.json()) as SensorResponse;
}

/** A precise packet factory preserves 0 and false instead of treating them as missing. */
function packet(capturedAtUtc: string, type: TelemetryDataType, value: number | boolean): Packet {
  return {
    capturedAtUtc,
    floatValue: type === 'Float' ? (value as number) : null,
    integerValue: type === 'Integer' ? (value as number) : null,
    booleanValue: type === 'Boolean' ? (value as boolean) : null,
  };
}

/** Submit one real jagged batch and require the server's accepted packet count. */
async function ingestBatch(
  request: APIRequestContext,
  sensor: SensorResponse,
  batches: readonly (readonly Packet[])[],
): Promise<void> {
  const response = await request.post(`${API_BASE}/api/sensors/${sensor.id}/telemetry/batches`, {
    data: { batches },
  });
  expect(response.status(), await response.text()).toBe(201);
  const result = (await response.json()) as { readonly acceptedPacketCount: number };
  expect(result.acceptedPacketCount).toBe(batches.reduce((sum, batch) => sum + batch.length, 0));
}

/** Convert known UTC instants to native datetime-local controls in the browser timezone. */
async function setRange(page: Page, fromUtc: string, toUtc: string): Promise<void> {
  const [from, to] = await page.evaluate(
    ([start, end]) =>
      [start, end].map((iso) => {
        const date = new Date(iso);
        return new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
          .toISOString()
          .slice(0, 16);
      }),
    [fromUtc, toUtc],
  );
  await page.getByLabel('From (local time)').fill(from);
  await page.getByLabel('To (local time)').fill(to);
}

/** Restrict option lookup to the authoritative selected-sensor control. */
async function selectSensor(page: Page, sensor: SensorResponse): Promise<void> {
  const selector = page.getByLabel('Select a registered sensor');
  await expect(selector.locator(`option[value="${sensor.id}"]`)).toHaveCount(1);
  await selector.selectOption(sensor.id);
  await expect(page.locator('.topology-history__identity')).toContainText(sensor.id);
}

function rangeEndpoint(sensor: SensorResponse, fromUtc: string, toUtc: string, limit: number) {
  const query = new URLSearchParams({ fromUtc, toUtc, limit: String(limit) });
  return `${API_BASE}/api/sensors/${sensor.id}/telemetry/range?${query}`;
}

test.describe('P3-09 final chronological AVL and cross-pillar regression', () => {
  test('orders out-of-order Float readings and pages across duplicate timestamps without losing a value', async ({
    page,
    request,
  }) => {
    const sensor = await registerSensor(request, 'Float');
    // This is deliberately NOT chronological ingestion. The equal-time bucket
    // must retain the original acceptance order -0.25, 12.375, 0.
    await ingestBatch(request, sensor, [
      [
        packet(LAST_UTC, 'Float', 7.75),
        packet(DUPLICATE_UTC, 'Float', -0.25),
        packet(DUPLICATE_UTC, 'Float', 12.375),
      ],
      [
        packet(THIRD_UTC, 'Float', 12.5),
        packet(FIRST_UTC, 'Float', -2.125),
        packet(DUPLICATE_UTC, 'Float', 0),
        packet(LAST_UTC, 'Float', 5.5),
      ],
    ]);

    const queriedUrls: string[] = [];
    page.on('request', (requestEvent) => {
      if (requestEvent.url().includes(`/api/sensors/${sensor.id}/telemetry/`)) {
        queriedUrls.push(requestEvent.url());
      }
    });

    await page.goto('/topology');
    await selectSensor(page, sensor);
    await setRange(page, FIRST_UTC, LAST_UTC);
    await page.getByLabel('Readings per page').selectOption('2');
    await page.getByRole('button', { name: 'Query AVL history' }).click();

    const rows = page.locator('.topology-history__table tbody tr');
    const values = page.locator('.topology-history__value');
    await expect(values).toHaveText(['-2.125', '-0.25']);
    await expect(rows).toHaveCount(2);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(values).toHaveText(['12.375', '0']);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(values).toHaveText(['12.5', '7.75']);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(values).toHaveText(['5.5']);
    await expect(page.getByRole('button', { name: 'Next page' })).toBeDisabled();
    await page.getByRole('button', { name: 'Previous page' }).click();
    await expect(values).toHaveText(['12.5', '7.75']);

    // No GET /telemetry (recent-history approximation) is allowed here.
    expect(queriedUrls).toHaveLength(5);
    const paths = queriedUrls.map((url) => new URL(url));
    for (const path of paths) {
      expect(path.pathname).toBe(`/api/sensors/${sensor.id}/telemetry/range`);
      expect(path.searchParams.get('limit')).toBe('2');
    }
    expect(paths[0].searchParams.has('afterUtc')).toBe(false);
    for (const path of paths.slice(1)) {
      expect(path.searchParams.has('afterUtc')).toBe(true);
      expect(path.searchParams.has('afterSequence')).toBe(true);
    }
    // The second page is reached by an exact timestamp+sequence cursor.
    expect(paths[1].searchParams.get('afterUtc')).not.toBeNull();
    expect(paths[1].searchParams.get('afterSequence')).not.toBeNull();

    // Inclusive narrow bounds must read the three equal-time values only.
    await setRange(page, DUPLICATE_UTC, DUPLICATE_UTC);
    await page.getByLabel('Readings per page').selectOption('25');
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(values).toHaveText(['-0.25', '12.375', '0']);
    await expect(page.getByRole('button', { name: 'Next page' })).toBeDisabled();
    await expect(page.locator('.topology-history__table')).toContainText('Float');
  });

  test('retains exact Integer zero/negative values and Boolean false/true for separate registered sensors', async ({
    page,
    request,
  }) => {
    const integerSensor = await registerSensor(request, 'Integer');
    const booleanSensor = await registerSensor(request, 'Boolean');
    await ingestBatch(request, integerSensor, [
      [packet(THIRD_UTC, 'Integer', -42), packet(FIRST_UTC, 'Integer', 0)],
    ]);
    await ingestBatch(request, booleanSensor, [
      [packet(THIRD_UTC, 'Boolean', true), packet(FIRST_UTC, 'Boolean', false)],
    ]);

    await page.goto('/topology');
    await selectSensor(page, integerSensor);
    await setRange(page, FIRST_UTC, LAST_UTC);
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText(['0', '-42']);
    await expect(page.locator('.topology-history__table')).toContainText('Integer');

    // Switching identity cancels the previous request and clears its rows.
    await selectSensor(page, booleanSensor);
    await expect(page.locator('.topology-history__table tbody tr')).toHaveCount(0);
    await setRange(page, FIRST_UTC, LAST_UTC);
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText(['False', 'True']);
    await expect(page.locator('.topology-history__table')).toContainText('Boolean');
    await expect(page.locator('.topology-history__identity')).toContainText(booleanSensor.id);
  });

  test('finds a real MAC device and retains its history bounds across the ingestion pillar and Back navigation', async ({
    page,
    request,
  }) => {
    const macSensor = await registerSensor(request, 'Float', { mac: true });
    await registerSensor(request, 'Float');
    await ingestBatch(request, macSensor, [[packet(FIRST_UTC, 'Float', 37.125)]]);

    await page.goto('/topology');
    await page
      .getByRole('searchbox', { name: 'Search mesh devices' })
      .fill(macSensor.deviceIdentifier);
    await page.getByRole('button', { name: 'Accessible table' }).click();
    const table = page.locator('.mesh-table');
    await expect(table.getByRole('row')).toHaveCount(2);
    await expect(table).toContainText(macSensor.deviceIdentifier);
    await table.getByRole('button', { name: `Select ${macSensor.name}` }).click();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(macSensor.id);
    await expect(page.locator('.topology-history__identity')).toContainText(
      macSensor.deviceIdentifier,
    );
    await setRange(page, FIRST_UTC, LAST_UTC);
    const savedFromLocal = await page.getByLabel('From (local time)').inputValue();
    const savedToLocal = await page.getByLabel('To (local time)').inputValue();
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText(['37.125']);

    const nav = page.getByRole('navigation', { name: 'Selected sensor workflows' });
    await nav.getByRole('link', { name: 'Submit telemetry' }).click();
    await expect(page).toHaveURL(/\/ingestion#telemetry-management$/);
    await expect(
      page.locator('.sensor-registry').getByRole('button', { name: new RegExp(macSensor.name) }),
    ).toHaveAttribute('aria-pressed', 'true');

    // Browser history does not initiate a new telemetry write or reset the
    // selected sensor/its safe date-range draft.
    await page.goBack();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(macSensor.id);
    await expect(page.getByLabel('From (local time)')).toHaveValue(savedFromLocal);
    await expect(page.getByLabel('To (local time)')).toHaveValue(savedToLocal);
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText(['37.125']);
  });

  test('rejects malformed and oversized direct AVL queries while preserving the correct indexed data', async ({
    request,
  }) => {
    const sensor = await registerSensor(request, 'Integer');
    await ingestBatch(request, sensor, [
      [packet(FIRST_UTC, 'Integer', 10), packet(DUPLICATE_UTC, 'Integer', 20)],
    ]);

    const endpoint = rangeEndpoint(sensor, FIRST_UTC, LAST_UTC, 2);
    const badQueries = [
      `${endpoint}&afterUtc=${encodeURIComponent(FIRST_UTC)}`,
      `${endpoint}&afterSequence=0`,
      `${endpoint}&afterUtc=${encodeURIComponent(FIRST_UTC)}&afterSequence=-1`,
      rangeEndpoint(sensor, LAST_UTC, FIRST_UTC, 2),
      rangeEndpoint(sensor, FIRST_UTC, LAST_UTC, 1001),
    ];

    for (const url of badQueries) {
      const response = await request.get(url);
      expect(response.status(), `Invalid AVL query should fail: ${url}`).toBe(400);
    }

    const valid = await request.get(endpoint);
    expect(valid.status()).toBe(200);
    const page = (await valid.json()) as TelemetryRangePageResponse;
    expect(page.sensorProfileId).toBe(sensor.id);
    expect(page.hasMore).toBe(false);
    expect(page.readings.map((reading) => reading.integerValue)).toEqual([10, 20]);
    expect(page.readings.every((reading) => reading.telemetryDataType === 'Integer')).toBe(true);
  });
});
