import { defineConfig, devices } from '@playwright/test';
import process from 'node:process';

/**
 * Dedicated HTTP API address used only by Playwright.
 *
 * The normal development application can continue using the HTTPS launch
 * profile on port 7095. E2E tests deliberately use an isolated HTTP port so
 * CI does not need to provision or trust a local development certificate.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';

/**
 * Smart-X Vite development address.
 *
 * The application already uses strict port 5173 behaviour, so Playwright can
 * safely treat this as a deterministic frontend endpoint.
 */
const clientBaseUrl = 'http://localhost:5173';

/**
 * Fixed 32-byte Base64 test key.
 *
 * This value is intentionally non-secret test material. It exists only so the
 * real encrypted attachment pipeline can run during automated E2E tests.
 * Production or personal encryption keys must never be committed.
 */
const testAttachmentEncryptionKey = 'AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=';

const isCi = Boolean(process.env.CI);

/**
 * Playwright configuration for Smart-X Part 1.
 *
 * Both the ASP.NET Core API and Vite client are started automatically. This
 * means npm run test:e2e exercises the real frontend-to-backend application
 * rather than a mocked approximation.
 */
export default defineConfig({
  testDir: './e2e',

  /*
   * Smart-X Part 1 repositories are intentionally process-local. Keeping one
   * worker avoids unnecessary concurrency between tests sharing the running
   * API process and makes CI failures deterministic.
   */
  fullyParallel: false,
  workers: 1,

  forbidOnly: isCi,
  retries: isCi ? 1 : 0,

  timeout: 60_000,

  expect: {
    timeout: 10_000,
  },

  reporter: isCi
    ? [
        ['line'],
        [
          'html',
          {
            open: 'never',
          },
        ],
      ]
    : [
        ['list'],
        [
          'html',
          {
            open: 'never',
          },
        ],
      ],

  use: {
    baseURL: clientBaseUrl,

    /*
     * Retain diagnostic evidence only when it is useful. Successful runs
     * remain lightweight while failures preserve enough information to debug
     * layout, network and browser-state problems.
     */
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
      },
    },
  ],

  /*
   * Playwright owns both development processes during E2E execution.
   *
   * Existing local servers are deliberately not reused. Reusing a manually
   * started Vite instance could cause it to retain the normal HTTPS API URL
   * instead of the isolated Playwright API URL below.
   */
  webServer: [
    {
      command:
        'dotnet run --project ../SmartX.Api --no-launch-profile --urls http://127.0.0.1:5095',

      url: `${apiBaseUrl}/api/health`,

      reuseExistingServer: false,
      timeout: 120_000,

      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        Attachments__EncryptionKeyBase64: testAttachmentEncryptionKey,
      },
    },
    {
      command: 'npm run dev -- --host localhost',

      url: clientBaseUrl,

      reuseExistingServer: false,
      timeout: 120_000,

      env: {
        VITE_API_BASE_URL: apiBaseUrl,
      },
    },
  ],
});
