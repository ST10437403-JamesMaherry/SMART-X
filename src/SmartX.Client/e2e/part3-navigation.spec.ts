import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';

/**
 * Playwright starts the real .NET API on this isolated address. These tests
 * intentionally exercise the shipped React client, not mocked components.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';
const topologyEndpoint = `${apiBaseUrl}/api/topology`;

/**
 * The same semantic navigation is present on all four client routes.
 * Keep role-based selectors independent of typography and CSS classes.
 */
const pillars = [
  {
    path: '/ingestion',
    name: /Sensor Data Ingestion and Telemetry/,
    heading: /Sensor Data\s*Ingestion/,
  },
  {
    path: '/realtime',
    name: /Real-Time Command Stream and History/,
    heading: /Real-Time\s*Command Stream/,
  },
  {
    path: '/topology',
    name: /Network Topology and Mesh Routing/,
    heading: /Network Topology/,
  },
] as const;

function moduleNavigation(page: Page) {
  return page.getByRole('navigation', { name: 'Smart-X architectural modules' });
}

/**
 * Each fixture is created through the existing production registration API.
 * Unique identifiers prevent clashes with other sequential Playwright tests
 * in the same process-local simulation.
 */
async function registerEnvironmentalSensor(request: APIRequestContext): Promise<SensorResponse> {
  const uniqueId = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P3 Navigation Sensor ${uniqueId}`,
      deviceIdentifier: `p3-nav-${uniqueId}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3 Navigation Facility',
        zone: 'P3 Regression Zone',
        nodeId: 'P3-01-Node',
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });

  expect(response.status()).toBe(201);
  return (await response.json()) as SensorResponse;
}

/**
 * The interaction count is server-owned. Merely viewing another pillar must
 * not be mistaken for an explicit operator search or command.
 */
async function recordedInteractionCount(request: APIRequestContext): Promise<number> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/interactions?limit=200`);
  expect(response.ok()).toBeTruthy();

  const result = (await response.json()) as { readonly totalRecorded: number };
  return result.totalRecorded;
}

test.describe('P3-01 integrated three-pillar navigation', () => {
  test('direct URLs, refresh and unknown routes keep three accessible module links', async ({
    page,
  }) => {
    for (const route of ['/', ...pillars.map((pillar) => pillar.path)]) {
      await page.goto(route);

      const nav = moduleNavigation(page);
      await expect(page.locator('main#main-content')).toHaveCount(1);
      await expect(nav.getByRole('link')).toHaveCount(3);
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

      // A route has exactly one active link, except for the landing page.
      const activeLink = nav.locator('[aria-current="page"]');
      await expect(activeLink).toHaveCount(route === '/' ? 0 : 1);

      for (const pillar of pillars) {
        const link = nav.getByRole('link', { name: pillar.name });
        await expect(link).toHaveAttribute('href', pillar.path);
        await expect(link).not.toHaveAttribute('aria-disabled', 'true');

        if (route === pillar.path) {
          await expect(link).toHaveAttribute('aria-current', 'page');
          await expect(page.getByRole('heading', { level: 1 })).toContainText(pillar.heading);
        } else {
          await expect(link).not.toHaveAttribute('aria-current', 'page');
        }
      }
    }

    // A hard reload of the new client route must resolve through Vite.
    await page.goto('/topology');
    await page.reload();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Network Topology');

    // The wildcard route redirects to the existing gateway, not a blank page.
    await page.goto('/not-a-smartx-module');
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('heading', { level: 1 })).toContainText('IoT Mesh');
    await expect(moduleNavigation(page).getByRole('link')).toHaveCount(3);
  });

  test('keyboard activation changes all pillars without a document reload', async ({ page }) => {
    await page.goto('/');
    const documentStart = await page.evaluate(() => performance.timeOrigin);

    for (const pillar of pillars) {
      const link = moduleNavigation(page).getByRole('link', { name: pillar.name });
      await link.focus();
      await expect(link).toBeFocused();
      await page.keyboard.press('Enter');

      await expect(page).toHaveURL(new RegExp(`${pillar.path}$`));
      await expect(link).toHaveAttribute('aria-current', 'page');
      await expect(page.getByRole('heading', { level: 1 })).toContainText(pillar.heading);
    }

    // Router navigation reuses the document and retains the app-level provider.
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });

  test('Back and Forward navigate all three pillars while retaining safe form drafts', async ({
    page,
  }) => {
    await page.goto('/ingestion');
    const registrationName = page.locator('form.sensor-registration-form [name="name"]');
    const draftText = `History draft ${randomUUID()}`;
    await registrationName.fill(draftText);
    const documentStart = await page.evaluate(() => performance.timeOrigin);

    await moduleNavigation(page).getByRole('link', { name: pillars[1].name }).click();
    await expect(page).toHaveURL(/\/realtime$/);
    await moduleNavigation(page).getByRole('link', { name: pillars[2].name }).click();
    await expect(page).toHaveURL(/\/topology$/);

    await page.goBack();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(
      moduleNavigation(page).getByRole('link', { name: pillars[1].name }),
    ).toHaveAttribute('aria-current', 'page');

    await page.goBack();
    await expect(page).toHaveURL(/\/ingestion$/);
    await expect(registrationName).toHaveValue(draftText);

    await page.goForward();
    await expect(page).toHaveURL(/\/realtime$/);
    await page.goForward();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(
      moduleNavigation(page).getByRole('link', { name: pillars[2].name }),
    ).toHaveAttribute('aria-current', 'page');
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });

  test('real sensor selection and typed drafts survive topology, but pending files do not', async ({
    page,
    request,
  }) => {
    const sensor = await registerEnvironmentalSensor(request);
    await page.goto('/ingestion');

    const sensorButton = page.getByRole('button', { name: new RegExp(sensor.name) });
    await sensorButton.click();
    await expect(sensorButton).toHaveAttribute('aria-pressed', 'true');

    const registrationName = page.locator('form.sensor-registration-form [name="name"]');
    const numericValue = page.locator('form.telemetry-form [name="numericValue"]');
    const firstBatch = page.locator('form.telemetry-batch-form').getByLabel('Batch 01', {
      exact: true,
    });
    const attachmentInput = page.locator('#attachment-file');
    const draftText = `Protected registration draft ${randomUUID()}`;

    await registrationName.fill(draftText);
    await numericValue.fill('38.75');
    await firstBatch.fill('18.5, 19.5');
    await attachmentInput.setInputFiles({
      name: 'part3-navigation-config.json',
      mimeType: 'application/json',
      buffer: Buffer.from('{"sampleRateSeconds":5,"enabled":true}'),
    });
    await expect(page.locator('.attachment-selection')).toContainText(
      'part3-navigation-config.json',
    );

    await moduleNavigation(page).getByRole('link', { name: pillars[2].name }).click();
    await expect(page).toHaveURL(/\/topology$/);
    await expect(attachmentInput).toHaveCount(0);

    await moduleNavigation(page).getByRole('link', { name: pillars[1].name }).click();
    await expect(page).toHaveURL(/\/realtime$/);
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(sensor.id);

    await moduleNavigation(page).getByRole('link', { name: pillars[0].name }).click();
    await expect(page).toHaveURL(/\/ingestion$/);
    await expect(sensorButton).toHaveAttribute('aria-pressed', 'true');
    await expect(registrationName).toHaveValue(draftText);
    await expect(numericValue).toHaveValue('38.75');
    await expect(firstBatch).toHaveValue('18.5, 19.5');

    // File objects must never be persisted beyond the owning uploader mount.
    await expect(attachmentInput).toHaveValue('');
    await expect(page.locator('.attachment-selection')).toHaveCount(0);

    // Navigating must not create telemetry or mutate the registered sensor.
    const persisted = await request.get(`${apiBaseUrl}/api/sensors/${sensor.id}`);
    expect(persisted.ok()).toBeTruthy();
    expect((await persisted.json()) as SensorResponse).toMatchObject({
      id: sensor.id,
      name: sensor.name,
      deviceIdentifier: sensor.deviceIdentifier,
    });
  });

  test('real-time search drafts survive topology without recording an operator interaction', async ({
    page,
    request,
  }) => {
    await page.goto('/realtime');
    await expect(page.getByRole('heading', { name: 'Find devices' })).toBeVisible();
    await expect(page.getByTestId('device-search-count')).toContainText('matching devices');

    const searchText = `unsubmitted-p3-${randomUUID()}`;
    await page.getByRole('searchbox', { name: 'Name / deployment text' }).fill(searchText);
    await page.getByRole('combobox', { name: 'Category' }).selectOption('Environmental');
    const interactionsBefore = await recordedInteractionCount(request);

    await moduleNavigation(page).getByRole('link', { name: pillars[2].name }).click();
    await expect(page).toHaveURL(/\/topology$/);
    await moduleNavigation(page).getByRole('link', { name: pillars[0].name }).click();
    await expect(page).toHaveURL(/\/ingestion$/);
    await moduleNavigation(page).getByRole('link', { name: pillars[1].name }).click();
    await expect(page).toHaveURL(/\/realtime$/);

    await expect(page.getByRole('searchbox', { name: 'Name / deployment text' })).toHaveValue(
      searchText,
    );
    await expect(page.getByRole('combobox', { name: 'Category' })).toHaveValue('Environmental');
    expect(await recordedInteractionCount(request)).toBe(interactionsBefore);
  });

  test('topology shows real API projections without inventing mesh connections', async ({
    page,
  }) => {
    await page.goto('/topology');

    // P3-08 now consumes the actual graph, BFS and MST endpoints instead of
    // keeping the old P3-01 health-only placeholder on screen.
    await expect(page.getByRole('heading', { name: 'Live mesh topology' })).toBeVisible();
    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();
    await expect(page.locator('.topology-bootstrap__stats')).toContainText('Configured links');
    await expect(page.locator('.topology-bootstrap__stats')).toContainText('Gateway reachable');
    await expect(
      moduleNavigation(page).getByRole('link', { name: pillars[2].name }),
    ).toHaveAttribute('aria-current', 'page');
  });

  test('topology API outage displays an actionable retry and recovers without navigation', async ({
    page,
  }) => {
    // Only intercept the browser's topology GET. Other tests continue to use
    // the real isolated ASP.NET Core API without shared server mutation.
    await page.route(topologyEndpoint, async (route) => {
      await route.abort('failed');
    });

    await page.goto('/topology');
    await expect(page.getByRole('alert')).toContainText(
      'The current topology could not be loaded.',
    );
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toBeVisible();
    await expect(page.locator('.topology-bootstrap__stats')).toHaveCount(0);

    const documentStart = await page.evaluate(() => performance.timeOrigin);
    await page.unroute(topologyEndpoint);
    await page.getByRole('button', { name: 'Retry topology request' }).click();

    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Retry topology request' })).toHaveCount(0);
    await expect(page).toHaveURL(/\/topology$/);
    expect(await page.evaluate(() => performance.timeOrigin)).toBe(documentStart);
  });

  test('topology navigation stays accessible at desktop, tablet and narrow mobile widths', async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/topology');
    await expect(page.getByRole('heading', { name: 'Live mesh topology' })).toBeVisible();
    await expect(page.getByText(/Graph revision \d+/)).toBeVisible();

    const widths = [1440, 768, 390, 320] as const;
    for (const width of widths) {
      await page.setViewportSize({ width, height: 900 });
      await expect(moduleNavigation(page).getByRole('link')).toHaveCount(3);

      for (const pillar of pillars) {
        await expect(moduleNavigation(page).getByRole('link', { name: pillar.name })).toBeVisible();
      }

      // A readable responsive layout must not scroll the entire document
      // sideways, even at the 320px accessibility/regression breakpoint.
      const horizontalOverflow = await page.evaluate(() => {
        const root = document.documentElement;
        return root.scrollWidth - root.clientWidth;
      });
      expect(horizontalOverflow, `unexpected horizontal scroll at ${width}px`).toBeLessThanOrEqual(
        1,
      );

      // Persist actual browser evidence in the Playwright HTML report.
      if (width === 1440 || width === 320) {
        await testInfo.attach(`part3-topology-${width}px`, {
          body: await page.screenshot({ fullPage: true }),
          contentType: 'image/png',
        });
      }
    }
  });
});
