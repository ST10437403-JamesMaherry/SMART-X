import { randomUUID } from 'node:crypto';

import { expect, test, type Page } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';
import type { TelemetryRangePageResponse } from '../src/api/telemetryRangeApi';

const API_BASE = 'http://127.0.0.1:5095';
const DATASET_SIZE = 10_000;
const TOTAL_ACCEPTED = DATASET_SIZE + 2;
const CHUNK_SIZE = 250;
const TARGET_ORDINAL = 5_000;
const START_UTC_MILLIS = Date.parse('2026-10-01T00:00:00.000Z');
const TARGET_UTC = new Date(START_UTC_MILLIS + TARGET_ORDINAL * 60_000).toISOString();

/**
 * An explicit opt-in protects the normal 90-ish-test browser regression from
 * large memory and CPU spikes. The Playwright server is still a genuine local
 * ASP.NET Core API, created by the project's existing webServer configuration.
 *
 * Run only when collecting the P3-09 10k+ dataset evidence:
 * SMARTX_P309_LARGE_DATASET=1 CI=true npm run test:e2e -- \
 *   --grep "P3-09 opt-in 10002-reading AVL evidence" --retries=0
 */
test.describe('P3-09 opt-in 10002-reading AVL evidence', () => {
  test.skip(
    process.env.SMARTX_P309_LARGE_DATASET !== '1',
    'Set SMARTX_P309_LARGE_DATASET=1 to run the resource-intensive 10,002-reading API test.',
  );

  test('accepts 10002 genuine packets and retrieves an exact narrow range through the AVL cursor', async ({
    page,
    request,
  }) => {
    test.setTimeout(360_000);
    const started = performance.now();

    const id = randomUUID();
    const registerResponse = await request.post(`${API_BASE}/api/sensors`, {
      data: {
        name: `P3-09 10002 AVL ${id}`,
        deviceIdentifier: `p309-10k-${id}`,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: {
          facility: 'P3-09 Verification Facility',
          zone: 'Large Indexed Dataset',
          nodeId: `p309-10k-${id}`,
        },
        category: 'Environmental',
        telemetryDataType: 'Integer',
      },
    });
    expect(registerResponse.status(), await registerResponse.text()).toBe(201);
    const sensor = (await registerResponse.json()) as SensorResponse;

    let accepted = 0;
    const acceptedPerRequest: number[] = [];

    // Ingestion order is descending capture time. The AVL index must sort by
    // timestamp, not by HTTP request arrival order. A chunk is small enough
    // to bound individual JSON allocations and network payloads.
    for (let offset = 0; offset < DATASET_SIZE; offset += CHUNK_SIZE) {
      const size = Math.min(CHUNK_SIZE, DATASET_SIZE - offset);
      const batch = Array.from({ length: size }, (_, position) => {
        const ordinal = DATASET_SIZE - 1 - (offset + position);
        return {
          capturedAtUtc: new Date(START_UTC_MILLIS + ordinal * 60_000).toISOString(),
          floatValue: null,
          integerValue: ordinal,
          booleanValue: null,
        };
      });
      const response = await request.post(
        `${API_BASE}/api/sensors/${sensor.id}/telemetry/batches`,
        {
          data: { batches: [batch] },
          timeout: 120_000,
        },
      );
      expect(response.status(), `API batch at index ${offset}: ${await response.text()}`).toBe(201);
      const receipt = (await response.json()) as { readonly acceptedPacketCount: number };
      expect(receipt.acceptedPacketCount).toBe(size);
      accepted += receipt.acceptedPacketCount;
      acceptedPerRequest.push(receipt.acceptedPacketCount);
    }

    // Append two exact equal-time readings after the ordinary reading at
    // TARGET_UTC. Their acceptance order must become the cursor sequence.
    const duplicates = await request.post(
      `${API_BASE}/api/sensors/${sensor.id}/telemetry/batches`,
      {
        data: {
          batches: [
            [
              {
                capturedAtUtc: TARGET_UTC,
                floatValue: null,
                integerValue: -701,
                booleanValue: null,
              },
              {
                capturedAtUtc: TARGET_UTC,
                floatValue: null,
                integerValue: -702,
                booleanValue: null,
              },
            ],
          ],
        },
        timeout: 120_000,
      },
    );
    expect(duplicates.status(), await duplicates.text()).toBe(201);
    const duplicateReceipt = (await duplicates.json()) as { readonly acceptedPacketCount: number };
    expect(duplicateReceipt.acceptedPacketCount).toBe(2);
    accepted += duplicateReceipt.acceptedPacketCount;
    expect(accepted).toBe(TOTAL_ACCEPTED);

    const query = new URLSearchParams({ fromUtc: TARGET_UTC, toUtc: TARGET_UTC, limit: '2' });
    const endpoint = `${API_BASE}/api/sensors/${sensor.id}/telemetry/range?${query}`;
    const firstResponse = await request.get(endpoint);
    expect(firstResponse.status()).toBe(200);
    const first = (await firstResponse.json()) as TelemetryRangePageResponse;
    expect(first.readings).toHaveLength(2);
    expect(first.readings.map((reading) => reading.integerValue)).toEqual([TARGET_ORDINAL, -701]);
    expect(
      first.readings.every(
        (reading) => Date.parse(reading.capturedAtUtc) === Date.parse(TARGET_UTC),
      ),
    ).toBe(true);
    expect(first.hasMore).toBe(true);
    expect(first.nextCursor).not.toBeNull();

    // Narrow the nullable cursor explicitly rather than hiding a broken API
    // response behind a non-null assertion in the verification code.
    const next = first.nextCursor;
    if (next === null) {
      throw new Error('Expected a composite continuation cursor for the duplicate minute.');
    }
    const continuation = new URLSearchParams({
      fromUtc: TARGET_UTC,
      toUtc: TARGET_UTC,
      limit: '2',
      afterUtc: next.capturedAtUtc,
      afterSequence: String(next.sequence),
    });
    const secondResponse = await request.get(
      `${API_BASE}/api/sensors/${sensor.id}/telemetry/range?${continuation}`,
    );
    expect(secondResponse.status()).toBe(200);
    const second = (await secondResponse.json()) as TelemetryRangePageResponse;
    expect(second.readings.map((reading) => reading.integerValue)).toEqual([-702]);
    expect(second.hasMore).toBe(false);
    expect(second.nextCursor).toBeNull();

    // Walk the complete indexed interval using bounded API pages, without
    // placing the whole series in browser state or assuming a total-count API.
    // This verifies the index contains ALL accepted packets, not only the
    // specially chosen three-row example near its middle.
    const wholeEndUtc = new Date(START_UTC_MILLIS + (DATASET_SIZE - 1) * 60_000).toISOString();
    const wholeFromUtc = new Date(START_UTC_MILLIS).toISOString();
    let totalIndexedRows = 0;
    let broadPageCount = 0;
    let lastSeenMillis = Number.NEGATIVE_INFINITY;
    let cursor: TelemetryRangePageResponse['nextCursor'] = null;
    while (true) {
      broadPageCount++;
      expect(
        broadPageCount,
        'More than 15 pages indicates an unexpected cursor loop',
      ).toBeLessThanOrEqual(15);
      const params = new URLSearchParams({
        fromUtc: wholeFromUtc,
        toUtc: wholeEndUtc,
        limit: '1000',
      });
      if (cursor !== null) {
        params.set('afterUtc', cursor.capturedAtUtc);
        params.set('afterSequence', String(cursor.sequence));
      }
      const response = await request.get(
        `${API_BASE}/api/sensors/${sensor.id}/telemetry/range?${params}`,
      );
      expect(response.status()).toBe(200);
      const indexedPage = (await response.json()) as TelemetryRangePageResponse;
      expect(indexedPage.readings.length).toBeLessThanOrEqual(1000);
      for (const reading of indexedPage.readings) {
        const time = Date.parse(reading.capturedAtUtc);
        expect(time).toBeGreaterThanOrEqual(lastSeenMillis);
        lastSeenMillis = time;
      }
      totalIndexedRows += indexedPage.readings.length;
      if (!indexedPage.hasMore) {
        expect(indexedPage.nextCursor).toBeNull();
        break;
      }
      expect(indexedPage.nextCursor).not.toBeNull();
      cursor = indexedPage.nextCursor;
    }
    expect(totalIndexedRows).toBe(TOTAL_ACCEPTED);

    // The same indexed result must be available in the browser with a page
    // size of two, without loading the 10,002-readings collection into React.
    const browserRequests: string[] = [];
    page.on('request', (browserRequest) => {
      if (browserRequest.url().includes(`/api/sensors/${sensor.id}/telemetry`)) {
        browserRequests.push(browserRequest.url());
      }
    });
    await page.goto('/topology');
    const selection = page.getByLabel('Select a registered sensor');
    await expect(selection.locator(`option[value="${sensor.id}"]`)).toHaveCount(1);
    await selection.selectOption(sensor.id);
    await expect(page.locator('.topology-history__identity')).toContainText(sensor.id);
    await chooseExactUtcMinute(page, TARGET_UTC);
    await page.getByLabel('Readings per page').selectOption('2');
    await page.getByRole('button', { name: 'Query AVL history' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText([
      String(TARGET_ORDINAL),
      '-701',
    ]);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(page.locator('.topology-history__value')).toHaveText(['-702']);
    expect(browserRequests).toHaveLength(2);
    expect(browserRequests.every((url) => new URL(url).pathname.endsWith('/telemetry/range'))).toBe(
      true,
    );

    const evidence = {
      issue: 'P3-09',
      sensorId: sensor.id,
      registeredTelemetryDataType: sensor.telemetryDataType,
      expectedAccepted: TOTAL_ACCEPTED,
      accepted,
      ingestionRequestCount: acceptedPerRequest.length + 1,
      ingestionChunkSize: CHUNK_SIZE,
      intentionallyOutOfOrder: true,
      narrowFromUtc: TARGET_UTC,
      narrowToUtc: TARGET_UTC,
      narrowPageLimit: 2,
      narrowValues: [...first.readings, ...second.readings].map((reading) => reading.integerValue),
      narrowRowCount: first.readings.length + second.readings.length,
      totalIndexedRows,
      broadPageCount,
      boundedApiPageLimit: 1000,
      elapsedMs: Math.round(performance.now() - started),
      nodeVersion: process.version,
      execution: 'real ASP.NET Core API managed by Playwright',
    };
    // This is factual, per-run evidence attached to the Playwright report;
    // it is never a hard-coded claim about universal performance.
    await test.info().attach('p309-avl-10002-verification.json', {
      body: JSON.stringify(evidence, null, 2),
      contentType: 'application/json',
    });
  });
});

async function chooseExactUtcMinute(page: Page, iso: string): Promise<void> {
  const local = await page.evaluate((timestamp) => {
    const date = new Date(timestamp);
    return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
  }, iso);
  await page.getByLabel('From (local time)').fill(local);
  await page.getByLabel('To (local time)').fill(local);
}
