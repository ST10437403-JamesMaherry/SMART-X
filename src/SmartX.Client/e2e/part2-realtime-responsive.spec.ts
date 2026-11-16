import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

/**
 * P2-07 Commit 4: real-browser mobile/desktop and accessible control evidence.
 *
 * The actual Smart-X API remains the source of all device state. Responsive
 * expectations test layout, keyboard semantics and reduced motion rather than
 * copying or generating visual mockups.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';

interface RegisteredDevice {
  readonly id: string;
  readonly name: string;
}

async function registerDevice(
  request: APIRequestContext,
  label: string,
  category: 'Actuator' | 'Environmental' = 'Environmental',
): Promise<RegisteredDevice> {
  const suffix = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P2-07 Responsive ${label} ${suffix.slice(0, 8)}`,
      deviceIdentifier: `p207-responsive-${suffix}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'Part 2 Test Facility',
        zone: 'Responsive Workspace',
        nodeId: `Node-${suffix.slice(0, 8)}`,
      },
      category,
      telemetryDataType: category === 'Actuator' ? 'Boolean' : 'Float',
    },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as RegisteredDevice;
}

async function openConsole(page: Page): Promise<void> {
  await page.goto('/realtime');
  await expect(page.getByRole('heading', { name: 'Operations console' })).toBeVisible();
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
}

async function selectDevice(page: Page, id: string): Promise<void> {
  const selector = page.getByRole('combobox', { name: 'Selected device' });
  await expect(selector.locator(`option[value="${id}"]`)).toHaveCount(1, { timeout: 15_000 });
  await selector.selectOption(id);
}

/**
 * Only a table's own scroll container should overflow on narrow screens.
 * A document-wide horizontal scrollbar would hide controls off-screen.
 */
async function expectNoDocumentHorizontalOverflow(page: Page): Promise<void> {
  const measured = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: window.innerWidth,
  }));
  expect(measured.documentWidth).toBeLessThanOrEqual(measured.viewportWidth + 2);
}

/**
 * Measure both columns in ONE browser execution context.
 *
 * P3-10 introduced independently loading network insights and operator-search
 * evidence above the operations grid. Their legitimate HTTP completions can
 * expand the page between two separate Playwright boundingBox() awaits, giving
 * two coordinates from different layout frames and a false desktop failure.
 *
 * Reading both DOMRects synchronously tests the real responsive invariant:
 * both operational panels must form one desktop row (or separate tablet rows)
 * at the SAME instant. This does not mock data or relax the alignment checks.
 */
async function measureOperationalColumns(page: Page) {
  return page
    .locator('.realtime-columns')
    .first()
    .evaluate((grid) => {
      const sections = grid.querySelectorAll(':scope > .realtime-section');
      const leftSection = sections.item(0);
      const rightSection = sections.item(1);

      if (sections.length !== 2 || !leftSection || !rightSection) {
        throw new Error('Expected exactly two real operational sections in the grid.');
      }

      const left = leftSection.getBoundingClientRect();
      const right = rightSection.getBoundingClientRect();
      return {
        display: window.getComputedStyle(grid).display,
        left: { x: left.x, y: left.y, width: left.width, height: left.height },
        right: { x: right.x, y: right.y, width: right.width, height: right.height },
      };
    });
}

test.describe('P2-07 responsive and accessible live workspace', () => {
  test('desktop shows the two real operational columns alongside each other', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openConsole(page);

    const columns = page.locator('.realtime-columns').first().locator(':scope > .realtime-section');
    await expect(columns).toHaveCount(2);

    // Atomic DOM geometry: asynchronous evidence above this grid cannot cause
    // an artificial vertical offset between independently awaited coordinates.
    const { display, left, right } = await measureOperationalColumns(page);
    expect(display).toBe('grid');
    expect(right.x).toBeGreaterThan(left.x + left.width);
    expect(Math.abs(right.y - left.y)).toBeLessThan(2);

    await expect(page.getByRole('heading', { name: 'Dispatch lanes' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'IoT traffic simulator' })).toBeVisible();
    await expectNoDocumentHorizontalOverflow(page);
  });

  test('tablet stacks operational columns but keeps a two-column scenario grid', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 768, height: 1024 });
    await openConsole(page);

    const columns = page.locator('.realtime-columns').first().locator(':scope > .realtime-section');
    await expect(columns).toHaveCount(2);

    // Also capture tablet positions in one layout frame: the content above
    // these sections can update independently while live HTTP requests resolve.
    const { display, left, right } = await measureOperationalColumns(page);
    expect(display).toBe('grid');
    expect(right.y).toBeGreaterThan(left.y + left.height);

    // Read both card positions in a single browser layout frame. Independent
    // boundingBox() calls can straddle live HTTP updates above this section,
    // producing a false vertical difference on an otherwise valid grid.
    const cards = page.locator('.realtime-scenario-grid > .realtime-scenario');
    await expect(cards).toHaveCount(3);
    const [cardA, cardB] = await cards.evaluateAll((elements) =>
      elements.slice(0, 2).map((element) => {
        const bounds = element.getBoundingClientRect();
        return { x: bounds.x, y: bounds.y };
      }),
    );
    expect(cardA).toBeDefined();
    expect(cardB).toBeDefined();
    expect(cardB.x).toBeGreaterThan(cardA.x);
    expect(Math.abs(cardB.y - cardA.y)).toBeLessThan(2);
    await expectNoDocumentHorizontalOverflow(page);
  });

  test('mobile retains commands and scrolls the real history table inside its own section', async ({
    page,
    request,
  }) => {
    const actuator = await registerDevice(request, 'MobileActuator', 'Actuator');
    const online = await request.patch(`${apiBaseUrl}/api/sensors/${actuator.id}/status`, {
      data: { status: 'Online' },
    });
    expect(online.status()).toBe(200);
    const relay = await request.post(`${apiBaseUrl}/api/realtime/devices/${actuator.id}/commands`, {
      data: { commandType: 'RelayOn' },
    });
    expect(relay.status()).toBe(200);

    await page.setViewportSize({ width: 390, height: 844 });
    await openConsole(page);
    await selectDevice(page, actuator.id);
    await expect(page.getByRole('heading', { name: actuator.name })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Undo latest command' })).toBeEnabled();
    await expect(page.getByRole('heading', { name: 'IoT traffic simulator' })).toBeVisible();

    const history = page.getByRole('table', { name: 'Chronological device history' });
    await expect(history).toContainText('Relay command');
    const scroll = await history.locator('..').evaluate((node) => ({
      clientWidth: node.clientWidth,
      scrollWidth: node.scrollWidth,
      overflowX: getComputedStyle(node).overflowX,
    }));
    expect(scroll.overflowX).toBe('auto');
    expect(scroll.scrollWidth).toBeGreaterThan(scroll.clientWidth);
    await expectNoDocumentHorizontalOverflow(page);
  });

  test('small mobile width keeps simulator inputs accessible and does not overflow the document', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    await openConsole(page);

    await expect(page.getByRole('spinbutton', { name: 'Load devices' })).toBeVisible();
    await expect(page.getByRole('spinbutton', { name: 'Readings per device' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Run finite load' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Pause ordinary dispatch' })).toBeVisible();

    const cards = page.locator('.realtime-scenario-grid > .realtime-scenario');
    const cardA = await cards.nth(0).boundingBox();
    const cardB = await cards.nth(1).boundingBox();
    expect(cardA).not.toBeNull();
    expect(cardB).not.toBeNull();
    // Two vertically stacked cards may share a boundary exactly.
    // Reject an overlap, not the valid zero-gap layout at 320px.
    expect(cardB!.y).toBeGreaterThanOrEqual(cardA!.y + cardA!.height);
    await expectNoDocumentHorizontalOverflow(page);
  });

  test('keyboard operators can reach labelled simulator controls with visible focus', async ({
    page,
  }) => {
    await openConsole(page);

    // The app-wide skip link must be first in keyboard order and usable with
    // the visible Part 2 page; mouse-only navigation is insufficient.
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Skip to content' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeVisible();
    await expect(skip).toHaveAttribute('href', '#main-content');

    const devices = page.getByRole('spinbutton', { name: 'Load devices' });
    const readings = page.getByRole('spinbutton', { name: 'Readings per device' });
    const submit = page.getByRole('button', { name: 'Run finite load' });

    await devices.focus();
    await page.keyboard.press('Tab');
    await expect(readings).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(submit).toBeFocused();

    const outline = await submit.evaluate((button) => getComputedStyle(button).outlineStyle);
    expect(outline).not.toBe('none');
    await expect(page.getByText(/10\s?000 planned readings/)).toBeVisible();
  });

  test('reduced-motion preference disables animations and transitions in the simulator', async ({
    page,
  }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await openConsole(page);

    const button = page.getByRole('button', { name: 'Run finite load' });
    const settings = await button.evaluate((node) => {
      const computed = getComputedStyle(node);
      return {
        prefersReducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
        animationName: computed.animationName,
        transitionDuration: computed.transitionDuration,
      };
    });

    expect(settings.prefersReducedMotion).toBe(true);
    expect(settings.animationName).toBe('none');
    expect(settings.transitionDuration).toBe('0s');
  });

  test('real configuration recovery clears only the selected device condition and retains empty history', async ({
    page,
    request,
  }) => {
    const device = await registerDevice(request, 'Recovery');
    await openConsole(page);
    await selectDevice(page, device.id);

    const conditionSection = page.locator('.realtime-conditions');
    await expect(
      conditionSection.locator('.realtime-condition').filter({ hasText: 'ConfigurationIssue' }),
    ).toHaveCount(1);
    await expect(
      page.getByText('No successful processing or accepted commands for this device.'),
    ).toBeVisible();

    // A real encrypted Configuration upload resolves the P2-06 HashSet key.
    // No synthetic response or direct Domain collection manipulation is used.
    const upload = await request.post(
      `${apiBaseUrl}/api/sensors/${device.id}/attachments?category=Configuration`,
      {
        multipart: {
          file: {
            name: 'p207-responsive-config.json',
            mimeType: 'application/json',
            buffer: Buffer.from('{"enabled":true,"sampleRateSeconds":5}'),
          },
        },
      },
    );
    expect(upload.status()).toBe(201);
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();

    await expect(conditionSection.getByText('No active conditions for this device.')).toBeVisible({
      timeout: 15_000,
    });
    await expect(conditionSection.locator('.realtime-condition')).toHaveCount(0);
    await expect(
      page.getByText('No successful processing or accepted commands for this device.'),
    ).toBeVisible();
  });
});
