import { Buffer } from 'node:buffer';

import { expect, test, type Page, type TestInfo } from '@playwright/test';

/**
 * Playwright-specific API endpoint configured in playwright.config.ts.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';

/**
 * Produces a valid locally administered MAC address that changes between test
 * executions.
 *
 * The API persists registered sensors for the lifetime of the test process.
 * Unique addresses prevent retries or sequential tests from accidentally
 * colliding with an earlier sensor.
 */
function createUniqueMac(testInfo: TestInfo, salt = 0): string {
  const seed = (Date.now() + testInfo.workerIndex * 101 + testInfo.retry * 37 + salt) & 0xffffff;

  const byte1 = (seed >> 16) & 0xff;
  const byte2 = (seed >> 8) & 0xff;
  const byte3 = seed & 0xff;

  /**
   * 02 identifies a locally administered unicast address and is appropriate
   * for simulated Part 1 test hardware.
   */
  return [
    '02',
    '00',
    '00',
    byte1.toString(16).padStart(2, '0'),
    byte2.toString(16).padStart(2, '0'),
    byte3.toString(16).padStart(2, '0'),
  ]
    .join(':')
    .toUpperCase();
}

/**
 * Registers one Float sensor entirely through the visible React form.
 *
 * Using the actual user interface rather than direct API setup means the E2E
 * path validates client-side form handling, transport integration, backend
 * persistence and resulting React state together.
 */
async function registerFloatSensor(page: Page, testInfo: TestInfo): Promise<string> {
  const sensorName = `Playwright Sensor ${Date.now()}`;

  const macAddress = createUniqueMac(testInfo, 1);

  await page.goto('/ingestion');

  await expect(
    page.getByRole('heading', {
      name: 'Sensor registry',
    }),
  ).toBeVisible();

  /*
   * Wait for the SignalR-backed Spotlight to be connected before registration
   * so the test can verify genuine live issue updates deterministically.
   */
  await expect(
    page.getByText('Live', {
      exact: true,
    }),
  ).toBeVisible({
    timeout: 15_000,
  });

  const registrationForm = page.locator('form.sensor-registration-form');

  await registrationForm.locator('[name="name"]').fill(sensorName);

  await registrationForm.locator('[name="identifierKind"]').selectOption('MacAddress');

  await registrationForm.locator('[name="deviceIdentifier"]').fill(macAddress);

  await registrationForm.locator('[name="facility"]').fill('Playwright Facility');

  await registrationForm.locator('[name="zone"]').fill('Regression Zone');

  await registrationForm.locator('[name="nodeId"]').fill('Node-E2E');

  await registrationForm.locator('[name="category"]').selectOption('Environmental');

  await registrationForm.locator('[name="telemetryDataType"]').selectOption('Float');

  await registrationForm
    .getByRole('button', {
      name: /register sensor/i,
    })
    .click();

  /*
   * The newly created sensor is automatically selected and therefore appears
   * in multiple contextual regions. The first visible exact match is
   * sufficient evidence that registration reached the rendered registry.
   */
  await expect(
    page
      .getByText(sensorName, {
        exact: true,
      })
      .first(),
  ).toBeVisible();

  return sensorName;
}

/**
 * Verifies that a form control has a genuine associated HTML label.
 *
 * This gives Issue #20 automated evidence that the registration workflow is
 * not relying only on placeholder text for accessibility.
 */
async function expectControlHasLabel(page: Page, selector: string): Promise<void> {
  const control = page.locator(selector).first();

  await expect(control).toBeVisible();

  const id = await control.getAttribute('id');

  if (!id) {
    throw new Error(`Control '${selector}' does not expose an id for label association.`);
  }

  await expect(page.locator(`label[for="${id}"]`)).toHaveCount(1);
}

test.describe('Smart-X Part 1 regression', () => {
  test('completes the core sensor, attachment, telemetry and anomaly journey', async ({
    page,
  }, testInfo) => {
    await registerFloatSensor(page, testInfo);

    /*
     * A newly registered sensor has no configuration evidence. The real
     * dashboard backend should therefore surface a configuration issue
     * through the live Spotlight.
     */
    const configurationIssue = page.locator('article.spotlight-issue').filter({
      hasText: 'Configuration evidence missing',
    });

    await expect(configurationIssue).toBeVisible();

    /*
     * Upload a valid configuration through the existing React file picker.
     * This exercises the real multipart -> validation -> AES-GCM persistence
     * path without creating a temporary local file on the CI machine.
     */
    const attachmentForm = page.locator('form.attachment-upload-form');

    await attachmentForm.locator('#attachment-category').selectOption('Configuration');

    await attachmentForm.locator('#attachment-file').setInputFiles({
      name: 'e2e-config.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify(
          {
            sampleRateSeconds: 5,
            enabled: true,
            source: 'playwright',
          },
          null,
          2,
        ),
      ),
    });

    await attachmentForm
      .getByRole('button', {
        name: 'Upload file',
      })
      .click();

    await expect(
      page
        .getByText('e2e-config.json', {
          exact: true,
        })
        .first(),
    ).toBeVisible();

    /*
     * Configuration issue resolution is pushed through SignalR. The browser
     * must update without a full page reload.
     */
    await expect(configurationIssue).toBeHidden({
      timeout: 15_000,
    });

    const telemetryForm = page.locator('form.telemetry-form');

    const telemetryValue = telemetryForm.locator('[name="numericValue"]');

    await telemetryValue.fill('100');

    await telemetryForm
      .getByRole('button', {
        name: 'Submit reading',
      })
      .click();

    await expect(
      page.getByText('Telemetry accepted.', {
        exact: true,
      }),
    ).toBeVisible();

    /*
     * The first valid telemetry packet proves connectivity and automatically
     * promotes the registered sensor from Offline to Online.
     */
    await expect(
      page.locator('.sensor-row--selected').getByText('Online', {
        exact: true,
      }),
    ).toBeVisible({
      timeout: 15_000,
    });

    /*
     * A rapid 100 -> 220 transition exceeds the critical anomaly threshold
     * and should surface through the real Adaptive Anomaly Spotlight.
     */
    await telemetryValue.fill('220');

    await telemetryForm
      .getByRole('button', {
        name: 'Submit reading',
      })
      .click();

    const criticalSpike = page.locator('article.spotlight-issue').filter({
      hasText: 'Critical telemetry spike',
    });

    await expect(criticalSpike).toBeVisible({
      timeout: 15_000,
    });

    await expect(
      criticalSpike.getByText('Critical', {
        exact: true,
      }),
    ).toBeVisible();

    /*
     * Acknowledgement must change operator state without falsely resolving the
     * underlying telemetry condition.
     */
    await criticalSpike
      .getByRole('button', {
        name: 'Acknowledge',
      })
      .click();

    await expect(
      criticalSpike.getByText('Acknowledged', {
        exact: true,
      }),
    ).toBeVisible();

    await expect(criticalSpike).toBeVisible();

    /*
     * Investigate should preserve the selected sensor and take the operator to
     * the relevant telemetry evidence.
     */
    await criticalSpike
      .getByRole('button', {
        name: 'Investigate',
      })
      .click();

    await expect(page).toHaveURL(/\/ingestion#telemetry-management$/);

    /*
     * Target the workspace-level telemetry heading precisely.
     *
     * Accessible-name matching is intentionally exact here because the page
     * also contains the headings "Telemetry simulator" and "Batch simulator".
     * Without exact matching, Playwright strict mode correctly considers all
     * three headings potential matches for "Simulator".
     */
    await expect(
      page.getByRole('heading', {
        name: 'Simulator',
        exact: true,
        level: 2,
      }),
    ).toBeVisible();
  });

  test('preserves accessible controls and responsive layouts', async ({ page }) => {
    await page.goto('/');

    /*
     * Part 1 must expose all three architectural pillars while leaving the two
     * future modules visibly and accessibly disabled.
     */
    const commandStreamControl = page.locator('button:disabled, [aria-disabled="true"]').filter({
      hasText: 'Real-Time Command Stream and History',
    });

    const meshRoutingControl = page.locator('button:disabled, [aria-disabled="true"]').filter({
      hasText: 'Network Topology and Mesh Routing',
    });

    await expect(commandStreamControl).toHaveCount(1);

    await expect(meshRoutingControl).toHaveCount(1);

    await page.goto('/ingestion');

    await expect(
      page.getByRole('heading', {
        name: 'Sensor registry',
      }),
    ).toBeVisible();

    /*
     * Verify real label associations rather than merely checking that text is
     * visible somewhere near each field.
     */
    await expectControlHasLabel(page, 'input[name="name"]');

    await expectControlHasLabel(page, 'select[name="identifierKind"]');

    await expectControlHasLabel(page, 'input[name="deviceIdentifier"]');

    await expectControlHasLabel(page, 'input[name="facility"]');

    await expectControlHasLabel(page, 'input[name="zone"]');

    await expectControlHasLabel(page, 'input[name="nodeId"]');

    await expectControlHasLabel(page, 'select[name="category"]');

    await expectControlHasLabel(page, 'select[name="telemetryDataType"]');

    /*
     * The four internal workspace links are native keyboard-focusable links.
     * Tab should move predictably from Spotlight to Sensors.
     */
    const spotlightLink = page.getByRole('link', {
      name: 'Spotlight',
      exact: true,
    });

    const sensorsLink = page.getByRole('link', {
      name: 'Sensors',
      exact: true,
    });

    await spotlightLink.focus();

    await expect(spotlightLink).toBeFocused();

    await page.keyboard.press('Tab');

    await expect(sensorsLink).toBeFocused();

    /*
     * Confirm the reduced-motion media query actually removes navigation
     * transitions when requested by the operating system/browser.
     */
    await page.emulateMedia({
      reducedMotion: 'reduce',
    });

    const transitionDuration = await spotlightLink.evaluate(
      (element) => window.getComputedStyle(element).transitionDuration,
    );

    expect(transitionDuration).toBe('0s');

    /*
     * Exercise the exact desktop, tablet and narrow mobile widths used during
     * manual Issue #20 review. Page-level horizontal scrolling is treated as a
     * regression.
     */
    const viewports = [
      {
        width: 1440,
        height: 900,
      },
      {
        width: 768,
        height: 1024,
      },
      {
        width: 390,
        height: 844,
      },
      {
        width: 320,
        height: 568,
      },
    ];

    for (const viewport of viewports) {
      await page.setViewportSize(viewport);

      const horizontalOverflow = await page.evaluate(() => {
        const root = document.documentElement;

        return root.scrollWidth - root.clientWidth;
      });

      expect(horizontalOverflow).toBeLessThanOrEqual(1);
    }
  });

  test('fails cleanly when the API is unavailable', async ({ page }) => {
    /*
     * Abort browser-side requests without stopping the Playwright-owned API
     * process. This keeps the failure deterministic and avoids affecting the
     * remaining test runner infrastructure.
     */
    await page.route(`${apiBaseUrl}/**`, async (route) => {
      await route.abort('failed');
    });

    await page.goto('/');

    await expect(
      page.getByRole('heading', {
        name: 'The API could not be reached',
      }),
    ).toBeVisible();

    await expect(
      page.getByRole('button', {
        name: 'Check again',
      }),
    ).toBeVisible();

    /*
     * The ingestion page should report the failure at the source—the sensor
     * registry—rather than presenting misleading downstream telemetry and
     * attachment interfaces.
     */
    await page.goto('/ingestion');

    await expect(
      page.getByRole('heading', {
        name: 'Sensor registry',
      }),
    ).toBeVisible();

    await expect(
      page
        .getByRole('button', {
          name: 'Try again',
        })
        .first(),
    ).toBeVisible();

    await expect(
      page.getByRole('heading', {
        name: 'Simulator',
      }),
    ).toHaveCount(0);

    await expect(
      page.getByRole('heading', {
        name: 'Device files',
      }),
    ).toHaveCount(0);
  });
});
