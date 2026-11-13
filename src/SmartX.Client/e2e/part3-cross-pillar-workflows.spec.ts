import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

const API_BASE = 'http://127.0.0.1:5095';

/**
 * Every test creates real sensor profiles through the ASP.NET Core API. The
 * backend is process-local and the full suite uses a single Playwright worker;
 * unique identifiers avoid reliance on global registration or event counts.
 */
async function registerWorkflowSensor(
  request: APIRequestContext,
  label: string,
): Promise<SensorResponse> {
  const uuid = randomUUID();
  const response = await request.post(`${API_BASE}/api/sensors`, {
    data: {
      name: `Workflow ${label} ${uuid}`,
      deviceIdentifier: `workflow-${uuid}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-09 Workflow Facility',
        zone: 'Cross-Pillar Integration',
        nodeId: `workflow-${uuid}`,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

/**
 * Wait for the real graph to contain the registered sensor before selecting.
 * No synthetic device fixtures are injected into the React page.
 */
async function selectInTopology(page: Page, sensor: SensorResponse): Promise<void> {
  const selector = page.getByLabel('Select a registered sensor');
  await expect(selector.locator(`option[value="${sensor.id}"]`)).toHaveCount(1);
  await selector.selectOption(sensor.id);
  await expect(page.locator('.topology-history__identity')).toContainText(sensor.id);
}

function workflowNav(page: Page) {
  return page.getByRole('navigation', { name: 'Selected sensor workflows' });
}

/** Read-only navigation must never create an implicit DeviceViewed episode. */
async function getRecordedInteractionCount(request: APIRequestContext): Promise<number> {
  const response = await request.get(`${API_BASE}/api/realtime/interactions?limit=200`);
  expect(response.ok()).toBeTruthy();
  const body = (await response.json()) as { readonly totalRecorded: number };
  return body.totalRecorded;
}

test.describe('P3-09 selected-device cross-pillar workflows', () => {
  test('retains one sensor and its AVL range draft through telemetry, commands and Back/Forward', async ({
    page,
    request,
  }) => {
    const sensor = await registerWorkflowSensor(request, 'Navigation');
    await page.goto('/topology');
    await selectInTopology(page, sensor);

    // These are safe date-range inputs only; no telemetry is submitted.
    const from = '2026-11-08T07:15';
    const to = '2026-11-09T09:45';
    await page.getByLabel('From (local time)').fill(from);
    await page.getByLabel('To (local time)').fill(to);
    await page.getByLabel('Readings per page').selectOption('50');

    const startingDocument = await page.evaluate(() => performance.timeOrigin);
    const interactionsBefore = await getRecordedInteractionCount(request);

    // An ingestion shortcut focuses the asynchronously mounted telemetry
    // section. The same server-owned selected ID powers its forms.
    await workflowNav(page).getByRole('link', { name: 'Submit telemetry' }).click();
    await expect(page).toHaveURL(/\/ingestion#telemetry-management$/);
    await expect(page.locator('#telemetry-management')).toBeFocused();
    await expect(
      page.locator('.sensor-registry').getByRole('button', { name: new RegExp(sensor.name) }),
    ).toHaveAttribute('aria-pressed', 'true');
    const numericDraft = page.locator('form.telemetry-form [name="numericValue"]');
    await numericDraft.fill('37.125');

    // Commands are merely inspectable on an environmental sensor; the
    // shortcut does not execute a relay command or claim it is eligible.
    await workflowNav(page).getByRole('link', { name: 'Real-time commands' }).click();
    await expect(page).toHaveURL(/\/realtime#realtime-command-title$/);
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(sensor.id);
    await expect(page.getByRole('heading', { name: 'Actuator commands' })).toBeFocused();
    await expect(page.getByText('This sensor is not an Actuator')).toBeVisible();

    // The realtime page also offers a direct deep link to the indexed view.
    await workflowNav(page).getByRole('link', { name: 'Indexed AVL history' }).click();
    await expect(page).toHaveURL(/\/topology#topology-history-heading$/);
    await expect(page.getByRole('heading', { name: 'Device history' })).toBeFocused();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(sensor.id);
    await expect(page.getByLabel('From (local time)')).toHaveValue(from);
    await expect(page.getByLabel('To (local time)')).toHaveValue(to);
    await expect(page.getByLabel('Readings per page')).toHaveValue('50');

    // Browser navigation restores the same provider without a document load.
    await page.goBack();
    await expect(page).toHaveURL(/\/realtime#realtime-command-title$/);
    await page.goBack();
    await expect(page).toHaveURL(/\/ingestion#telemetry-management$/);
    await expect(numericDraft).toHaveValue('37.125');
    await page.goBack();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByLabel('From (local time)')).toHaveValue(from);
    await expect(page.getByLabel('Readings per page')).toHaveValue('50');

    expect(await getRecordedInteractionCount(request)).toBe(interactionsBefore);
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(startingDocument);
  });

  test('honors explicit real-time selection when following topology and registry shortcuts', async ({
    page,
    request,
  }) => {
    await registerWorkflowSensor(request, 'Other');
    const chosen = await registerWorkflowSensor(request, 'Selected');

    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    const devicePicker = page.getByRole('combobox', { name: 'Selected device' });
    await devicePicker.selectOption(chosen.id);
    await expect(devicePicker).toHaveValue(chosen.id);

    await workflowNav(page).getByRole('link', { name: 'Topology routes' }).click();
    await expect(page).toHaveURL(/\/topology#topology-workspace-title$/);
    await expect(page.getByRole('heading', { name: 'Explore the mesh' })).toBeFocused();
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(chosen.id);
    await expect(page.locator('.mesh-details')).toContainText(chosen.deviceIdentifier);

    await workflowNav(page).getByRole('link', { name: 'Sensor registry' }).click();
    await expect(page).toHaveURL(/\/ingestion#sensor-management$/);
    await expect(page.locator('#sensor-management')).toBeFocused();
    await expect(
      page.locator('.sensor-registry').getByRole('button', { name: new RegExp(chosen.name) }),
    ).toHaveAttribute('aria-pressed', 'true');

    // IDs are held in the existing provider; we never embed them in links,
    // rewrite a sensor profile, or re-register the selected sensor.
    expect(new URL(page.url()).searchParams.has('sensorId')).toBe(false);
  });

  test('retains independent per-device history drafts without leaking results at 375px', async ({
    page,
    request,
  }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    const alpha = await registerWorkflowSensor(request, 'Alpha');
    const beta = await registerWorkflowSensor(request, 'Beta');
    await page.goto('/topology');

    await selectInTopology(page, alpha);
    await page.getByLabel('From (local time)').fill('2026-11-01T07:00');
    await page.getByLabel('To (local time)').fill('2026-11-02T08:00');
    await page.getByLabel('Readings per page').selectOption('100');

    await selectInTopology(page, beta);
    await expect(page.getByLabel('From (local time)')).not.toHaveValue('2026-11-01T07:00');
    await page.getByLabel('From (local time)').fill('2026-11-03T09:00');
    await page.getByLabel('To (local time)').fill('2026-11-04T10:00');
    await page.getByLabel('Readings per page').selectOption('2');

    await selectInTopology(page, alpha);
    await expect(page.getByLabel('From (local time)')).toHaveValue('2026-11-01T07:00');
    await expect(page.getByLabel('To (local time)')).toHaveValue('2026-11-02T08:00');
    await expect(page.getByLabel('Readings per page')).toHaveValue('100');

    await selectInTopology(page, beta);
    await expect(page.getByLabel('From (local time)')).toHaveValue('2026-11-03T09:00');
    await expect(page.getByLabel('To (local time)')).toHaveValue('2026-11-04T10:00');
    await expect(page.getByLabel('Readings per page')).toHaveValue('2');
    // No prior device's AVL page is retained or auto-fetched on selection.
    await expect(page.getByText(/Choose a time range and query the selected sensor/)).toBeVisible();
    await expect(page.locator('.topology-history__table tbody tr')).toHaveCount(0);

    const documentOverflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(documentOverflow).toBeLessThanOrEqual(1);
  });
});
