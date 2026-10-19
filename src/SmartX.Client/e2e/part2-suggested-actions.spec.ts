import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Route } from '@playwright/test';

import type { PredictedRecommendationsResponse } from '../src/api/recommendationsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';
const recommendationsPath = '**/api/realtime/recommendations';

/**
 * This method reads real interaction history. Merely loading the new
 * recommendation panel must not generate deliberate operator evidence.
 */
async function interactionCount(request: APIRequestContext): Promise<number> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/interactions?limit=1`);
  expect(response.status()).toBe(200);
  const history = (await response.json()) as { totalRecorded: number };
  return history.totalRecorded;
}

/**
 * A presentation-only transport fixture. This is NOT a claim that training
 * occurred: Commit 3 will exercise three genuine server-side learning episodes
 * and verify real recommendations without intercepting their responses.
 */
function presentationFixture(expiresAtUtc: string): PredictedRecommendationsResponse {
  return {
    evaluatedAtUtc: new Date().toISOString(),
    minimumSupportingEpisodes: 3,
    evidenceWindowSeconds: 604_800,
    nextRefreshAtUtc: expiresAtUtc,
    recommendations: [
      {
        sourceAlertEpisodeId: randomUUID(),
        sourceDeviceId: randomUUID(),
        sourceDeviceName: 'E2E Source B',
        conditionType: 'TelemetrySpike',
        severity: 'Critical',
        movement: 'Decrease',
        targetActuatorId: randomUUID(),
        targetActuatorName: 'E2E Pump Relay A',
        commandType: 'RelayOn',
        supportingEpisodes: 3,
        lastLearnedAtUtc: new Date().toISOString(),
        score: 85,
        confidenceBand: 'High',
        expiresAtUtc,
        explanation: 'Three distinct alert episodes led to a successful RelayOn action.',
      },
    ],
  };
}

test.describe('P2-10 Suggested Actions read-only presentation', () => {
  test('real API starts with honest insufficient-history guidance and no passive interactions', async ({
    page,
    request,
  }) => {
    const before = await interactionCount(request);
    const authoritativeRead = page.waitForResponse(
      (response) =>
        response.url().endsWith('/api/realtime/recommendations') && response.status() === 200,
    );

    await page.goto('/realtime');
    await expect(page.getByRole('heading', { name: 'Suggested Actions' })).toBeVisible();
    await authoritativeRead;
    await expect(page.getByTestId('suggested-actions-empty')).toBeVisible();
    await expect(page.getByTestId('suggested-actions-count')).toHaveText('0 eligible');
    await expect(page.getByTestId('suggested-action')).toHaveCount(0);
    expect(await interactionCount(request)).toBe(before);
  });

  test('HTTP failure provides a retry and never fabricates recommendations', async ({ page }) => {
    let blockedRecommendationReads = 0;
    const failRecommendationRead = async (route: Route): Promise<void> => {
      blockedRecommendationReads += 1;
      await route.abort('failed');
    };
    await page.route(recommendationsPath, failRecommendationRead);
    await page.goto('/realtime');

    const panel = page.getByTestId('suggested-actions');
    const retrySuggestions = panel.getByRole('button', { name: 'Retry suggestions' });
    await expect(panel.getByRole('alert')).toBeVisible();
    await expect(panel.getByTestId('suggested-action')).toHaveCount(0);

    // Verify that the Retry control really issues a new GET while the outage
    // persists. Keep the route blocked until the click is completed: otherwise
    // a SignalR refresh can recover and detach this transient button mid-click.
    const failuresBeforeRetry = blockedRecommendationReads;
    await retrySuggestions.click();
    await expect
      .poll(() => blockedRecommendationReads, {
        message: 'Manual Retry must request a fresh recommendation evaluation',
      })
      .toBeGreaterThan(failuresBeforeRetry);
    await expect(panel.getByRole('alert')).toBeVisible();
    await expect(retrySuggestions).toBeVisible();

    // Restore real HTTP and request another evaluation through the permanent
    // refresh control. Never mock a successful prediction response here.
    await page.unroute(recommendationsPath, failRecommendationRead);
    await panel.getByRole('button', { name: 'Refresh suggestions' }).click();
    await expect(panel.getByTestId('suggested-actions-empty')).toBeVisible();
    await expect(panel.getByRole('alert')).toHaveCount(0);
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
  });

  test('renders the exact P2-09 response contract without issuing relay commands', async ({
    page,
  }) => {
    const fixture = presentationFixture(new Date(Date.now() + 60_000).toISOString());
    const executedCommands: string[] = [];

    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );
    page.on('request', (request) => {
      if (
        request.method() === 'POST' &&
        /\/api\/realtime\/devices\/[^/]+\/commands/.test(request.url())
      ) {
        executedCommands.push(request.url());
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    const action = panel.getByTestId('suggested-action');

    await expect(action).toHaveCount(1);
    await expect(action).toContainText('Relay On: E2E Pump Relay A');
    await expect(action).toContainText('3 distinct episodes');
    await expect(action).toContainText('High confidence / 85 of 100');
    await expect(action).toContainText('Critical TelemetrySpike on E2E Source B / decrease');
    await expect(action).toContainText(fixture.recommendations[0].explanation);
    await expect(panel.getByTestId('suggested-actions-count')).toHaveText('1 eligible');
    // Commit 2 adds an explicit review control, NOT automatic execution.
    await expect(action.getByRole('button', { name: 'Review Relay On' })).toBeVisible();
    await action.getByRole('button', { name: 'Review Relay On' }).click();
    await expect(action.getByRole('button', { name: 'Confirm Relay On' })).toBeVisible();
    await action.getByRole('button', { name: 'Cancel' }).click();
    expect(executedCommands).toEqual([]);
  });

  test('SignalR invalidation triggers a fresh recommendation GET from the server', async ({
    page,
    request,
  }) => {
    let reads = 0;
    page.on('request', (req) => {
      if (req.method() === 'GET' && req.url().endsWith('/api/realtime/recommendations')) {
        reads += 1;
      }
    });

    await page.goto('/realtime');
    await expect(page.getByTestId('suggested-actions-empty')).toBeVisible();
    await expect(page.getByText('SignalR connected')).toBeVisible();
    const beforeChange = reads;

    // Real sensor registration emits an existing realtime invalidation.
    const uniqueId = randomUUID();
    const response = await request.post(`${apiBaseUrl}/api/sensors`, {
      data: {
        name: `P210 Invalidation ${uniqueId}`,
        deviceIdentifier: `p210-${uniqueId}`,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: {
          facility: 'P2-10 Operations Lab',
          zone: 'Refresh Tests',
          nodeId: `Node-${uniqueId}`,
        },
        category: 'Environmental',
        telemetryDataType: 'Float',
      },
    });
    expect(response.status()).toBe(201);

    await expect.poll(() => reads, { timeout: 15_000 }).toBeGreaterThan(beforeChange);
    await expect(page.getByTestId('suggested-actions-empty')).toBeVisible();
  });

  test('a reached server expiry removes guidance without a new command or search', async ({
    page,
  }) => {
    const expiresAtUtc = new Date(Date.now() + 1_800).toISOString();
    const fixture = presentationFixture(expiresAtUtc);
    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    await expect(panel.getByTestId('suggested-action')).toHaveCount(1);

    // The clock advances normally; the expiry timer and passive HTTP refresh
    // must retire this fixture even though no new SignalR event arrives.
    await expect(panel.getByTestId('suggested-action')).toHaveCount(0, { timeout: 10_000 });
    await expect(panel.getByTestId('suggested-actions-empty')).toBeVisible();
  });
});
