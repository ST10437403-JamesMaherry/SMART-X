import { randomBytes, randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { OperatorInteractionHistoryResponse } from '../src/api/operatorInteractionsApi';
import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';

/** Unique physical identifiers prevent cross-test collisions in the shared API. */
function uniqueMacAddress(): string {
  return `02:${randomBytes(5).toString('hex').match(/../g)!.join(':')}`.toUpperCase();
}

async function registerDevice(
  request: APIRequestContext,
  name: string,
  category: 'Environmental' | 'Actuator' = 'Environmental',
  identifierKind: 'MacAddress' | 'UniqueIdentifier' = 'UniqueIdentifier',
): Promise<SensorResponse> {
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name,
      deviceIdentifier:
        identifierKind === 'MacAddress' ? uniqueMacAddress() : `p208-${randomUUID()}`,
      identifierKind,
      deploymentLocation: {
        facility: 'P2-08 E2E Lab',
        zone: 'Search Tests',
        nodeId: `Node-${randomUUID()}`,
      },
      category,
      telemetryDataType: category === 'Actuator' ? 'Boolean' : 'Float',
    },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

async function changeStatus(
  request: APIRequestContext,
  deviceId: string,
  status: 'Online' | 'Offline',
): Promise<void> {
  const response = await request.patch(`${apiBaseUrl}/api/sensors/${deviceId}/status`, {
    data: { status },
  });
  expect(response.status()).toBe(200);
}

/** This read-only endpoint must itself never increment TotalRecorded. */
async function readInteractions(
  request: APIRequestContext,
): Promise<OperatorInteractionHistoryResponse> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/interactions?limit=200`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as OperatorInteractionHistoryResponse;
}

async function openSearch(page: Page): Promise<void> {
  await page.goto('/realtime');
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Find devices' })).toBeVisible();
  await expect(page.getByTestId('device-search-count')).toContainText('matching devices');
}

/** Uses the real input. Typing is not the same as submitting the query. */
async function enterText(page: Page, text: string): Promise<void> {
  await page.getByRole('searchbox', { name: 'Name / deployment text' }).fill(text);
}

test.describe('P2-08 canonical discovery and deliberate operator history', () => {
  test('mount, typing, automatic HTTP refresh and passive result GETs record no actions', async ({
    page,
    request,
  }) => {
    const before = await readInteractions(request);
    await openSearch(page);
    await enterText(page, `unsubmitted-${randomUUID()}`);
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();

    const after = await readInteractions(request);
    expect(after.totalRecorded).toBe(before.totalRecorded);
  });

  test('canonical MAC plus category/status uses one server search and explicit device view', async ({
    page,
    request,
  }) => {
    const name = `P208 MAC ${randomUUID()}`;
    const sensor = await registerDevice(request, name, 'Environmental', 'MacAddress');
    await changeStatus(request, sensor.id, 'Online');
    await openSearch(page);

    // Deliberately submit an equivalent physical MAC spelling, not an ID scan.
    await page
      .getByRole('textbox', { name: 'Exact physical identifier' })
      .fill(sensor.deviceIdentifier.toLowerCase().replaceAll(':', '-'));
    await page.getByRole('combobox', { name: 'Category' }).selectOption('Environmental');
    await page.getByRole('combobox', { name: 'Operational status' }).selectOption('Online');
    await page.getByRole('button', { name: 'Search devices' }).click();

    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await expect(page.getByTestId('device-search-result')).toContainText(name);
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('SearchSubmitted');

    let history = await readInteractions(request);
    expect(history.events[0]).toMatchObject({
      kind: 'SearchSubmitted',
      canonicalIdentifier: sensor.deviceIdentifier,
      category: 'Environmental',
      status: 'Online',
      resultCount: 1,
    });

    await page.getByRole('button', { name: 'Inspect device' }).click();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(sensor.id);
    await expect
      .poll(async () => (await readInteractions(request)).events[0]?.kind)
      .toBe('DeviceViewed');

    history = await readInteractions(request);
    expect(history.events[0].deviceId).toBe(sensor.id);
    expect(history.events[0].occurredAtUtc).toBeTruthy();
    const countAfterView = history.totalRecorded;

    // SignalR and HTTP refreshes are not claims of a second operator view.
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    expect((await readInteractions(request)).totalRecorded).toBe(countAfterView);
  });

  test('explicit combined filter changes are logged; a zero-result page is valid', async ({
    page,
    request,
  }) => {
    const name = `P208 Empty ${randomUUID()}`;
    await registerDevice(request, name, 'Actuator');
    await openSearch(page);
    const initialCount = (await readInteractions(request)).totalRecorded;

    await enterText(page, name);
    await page.getByRole('combobox', { name: 'Category' }).selectOption('Actuator');
    await page.getByRole('combobox', { name: 'Operational status' }).selectOption('Online');
    await page.getByRole('button', { name: 'Apply filters' }).click();

    await expect(page.getByTestId('device-search-count')).toHaveText('0 matching devices');
    await expect(page.getByTestId('device-search-empty')).toBeVisible();
    const accepted = await readInteractions(request);
    expect(accepted.totalRecorded).toBe(initialCount + 1);
    expect(accepted.events[0]).toMatchObject({ kind: 'FiltersChanged', resultCount: 0 });
    await expect(page.getByRole('button', { name: 'Apply filters' })).toBeDisabled();

    // Another filter change is a second deliberate act; a passive GET is not.
    await page.getByRole('combobox', { name: 'Operational status' }).selectOption('Offline');
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    expect((await readInteractions(request)).totalRecorded).toBe(initialCount + 2);
  });

  test('submitted criteria, draft fields and selected device survive route navigation', async ({
    page,
    request,
  }) => {
    const name = `P208 Retain ${randomUUID()}`;
    const device = await registerDevice(request, name);
    await openSearch(page);
    await enterText(page, name);
    await page.getByRole('combobox', { name: 'Category' }).selectOption('Environmental');
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await page.getByRole('button', { name: 'Inspect device' }).click();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(device.id);
    const count = (await readInteractions(request)).totalRecorded;

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();

    await expect(page.getByRole('searchbox', { name: 'Name / deployment text' })).toHaveValue(name);
    await expect(page.getByRole('combobox', { name: 'Category' })).toHaveValue('Environmental');
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(device.id);
    expect((await readInteractions(request)).totalRecorded).toBe(count);
  });

  test('malformed exact MAC returns ProblemDetails without changing results or recording evidence', async ({
    page,
    request,
  }) => {
    await openSearch(page);
    const before = await readInteractions(request);
    await page.getByRole('textbox', { name: 'Exact physical identifier' }).fill('invalid-mac');
    await page.getByRole('button', { name: 'Search devices' }).click();

    await expect(page.getByRole('alert')).toContainText('Search rejected or unavailable');
    await expect(page.getByTestId('device-search-count')).toBeVisible();
    expect((await readInteractions(request)).totalRecorded).toBe(before.totalRecorded);
  });

  test('an explicit source alert view can be associated with a searched actuator', async ({
    page,
    request,
  }) => {
    const source = await registerDevice(request, `P208 Alert Source ${randomUUID()}`);
    await changeStatus(request, source.id, 'Online');
    await changeStatus(request, source.id, 'Offline');
    const target = await registerDevice(request, `P208 Alert Target ${randomUUID()}`, 'Actuator');

    await openSearch(page);
    await enterText(page, source.name);
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await expect(page.getByRole('button', { name: 'View active alert' })).toBeVisible();
    await page.getByRole('button', { name: 'View active alert' }).click();
    await expect(page.getByText('Source alert selected')).toBeVisible();

    const alertView = (await readInteractions(request)).events[0];
    expect(alertView.kind).toBe('AlertViewed');
    expect(alertView.deviceId).toBe(source.id);
    expect(alertView.alertEpisodeId).toBeTruthy();

    await enterText(page, target.name);
    await page.getByRole('combobox', { name: 'Category' }).selectOption('Actuator');
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await page.getByRole('button', { name: 'Associate with source alert' }).click();
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('source alert episode');
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);

    const association = (await readInteractions(request)).events[0];
    expect(association.kind).toBe('SearchSubmitted');
    expect(association.deviceId).toBe(target.id);
    expect(association.alertEpisodeId).toBe(alertView.alertEpisodeId);
    // No relay command was attempted or falsely claimed by the client.
    expect(association.commandId).toBeNull();
  });

  test('real active-condition filters update after server recovery without another POST', async ({
    page,
    request,
  }) => {
    const device = await registerDevice(request, `P208 Condition ${randomUUID()}`);
    await changeStatus(request, device.id, 'Online');
    await changeStatus(request, device.id, 'Offline');
    await openSearch(page);

    await enterText(page, device.name);
    await page.getByRole('combobox', { name: 'Operational status' }).selectOption('Offline');
    await page.getByRole('combobox', { name: 'Active conditions' }).selectOption('true');
    await page.getByRole('combobox', { name: 'Condition type' }).selectOption('SensorDisconnected');
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    const count = (await readInteractions(request)).totalRecorded;

    // Subscribe before recovery: otherwise an already completed status PATCH
    // could race the hub handshake and make this test flaky.
    await expect(page.getByText('SignalR connected')).toBeVisible();
    await changeStatus(request, device.id, 'Online');
    await expect(page.getByTestId('device-search-count')).toHaveText('0 matching devices', {
      timeout: 15_000,
    });
    expect((await readInteractions(request)).totalRecorded).toBe(count);
  });

  test('paging large result sets reads new pages without creating further interactions', async ({
    page,
    request,
  }) => {
    const prefix = `P208 Page ${randomUUID()}`;
    // A bounded 27-device scenario spans two real default-size (25) pages.
    for (let index = 0; index < 27; index += 1) {
      await registerDevice(request, `${prefix} ${index}`);
    }
    await openSearch(page);
    await enterText(page, prefix);
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('27 matching devices');
    await expect(page.getByTestId('device-search-result')).toHaveCount(25);
    const count = (await readInteractions(request)).totalRecorded;

    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(page.getByTestId('device-search-result')).toHaveCount(2);
    await expect(page.getByText('Page 2')).toBeVisible();
    await page.getByRole('button', { name: 'Previous page' }).click();
    await expect(page.getByTestId('device-search-result')).toHaveCount(25);
    expect((await readInteractions(request)).totalRecorded).toBe(count);
  });
});
