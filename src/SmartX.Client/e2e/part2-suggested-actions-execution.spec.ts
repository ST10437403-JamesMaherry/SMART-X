import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { PredictedRecommendationsResponse } from '../src/api/recommendationsApi';
import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';
const recommendationsPath = '**/api/realtime/recommendations';

/**
 * The transport fixture supplies a suggested-action PRESENTATION only.
 * Successful relay commands and Stack-backed Undo go to the REAL .NET API.
 * Commit 3 will replace the recommendation fixture with genuine learning.
 */
function presentationFixture(target: SensorResponse): PredictedRecommendationsResponse {
  const expiresAtUtc = new Date(Date.now() + 300_000).toISOString();
  return {
    evaluatedAtUtc: new Date().toISOString(),
    minimumSupportingEpisodes: 3,
    evidenceWindowSeconds: 604_800,
    nextRefreshAtUtc: expiresAtUtc,
    recommendations: [
      {
        sourceAlertEpisodeId: randomUUID(),
        sourceDeviceId: randomUUID(),
        sourceDeviceName: 'P210 Simulated Moisture Source',
        conditionType: 'TelemetrySpike',
        severity: 'Critical',
        movement: 'Decrease',
        targetActuatorId: target.id,
        targetActuatorName: target.name,
        commandType: 'RelayOn',
        supportingEpisodes: 3,
        lastLearnedAtUtc: new Date().toISOString(),
        score: 85,
        confidenceBand: 'High',
        expiresAtUtc,
        explanation: 'Presentation fixture for an operator-approved relay command.',
      },
    ],
  };
}

/** Register a fresh actuator so each test has independent commanded state. */
async function registerOnlineActuator(request: APIRequestContext): Promise<SensorResponse> {
  const suffix = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `P210 Command Target ${suffix}`,
      deviceIdentifier: `p210-command-${suffix}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P2-10 Operations Lab',
        zone: 'Operator Confirmation',
        nodeId: `Node-${suffix}`,
      },
      category: 'Actuator',
      telemetryDataType: 'Boolean',
    },
  });
  expect(response.status()).toBe(201);
  const target = (await response.json()) as SensorResponse;
  const changed = await request.patch(`${apiBaseUrl}/api/sensors/${target.id}/status`, {
    data: { status: 'Online' },
  });
  expect(changed.status()).toBe(200);
  return target;
}

async function getCommandAudit(
  request: APIRequestContext,
  deviceId: string,
): Promise<{ totalAuditCount: number; actuator: { commandedState: string; undoDepth: number } }> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/devices/${deviceId}/commands`);
  expect(response.status()).toBe(200);
  return (await response.json()) as {
    totalAuditCount: number;
    actuator: { commandedState: string; undoDepth: number };
  };
}

/** Verify the UI does not invent a DeviceViewed/SearchSubmitted interaction. */
async function interactionCount(request: APIRequestContext): Promise<number> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/interactions?limit=1`);
  expect(response.status()).toBe(200);
  const history = (await response.json()) as { totalRecorded: number };
  return history.totalRecorded;
}

test.describe('P2-10 operator-approved Suggested Actions', () => {
  test('review, cancel and reopen remain passive without creating commands or training evidence', async ({
    page,
    request,
  }) => {
    const target = await registerOnlineActuator(request);
    const fixture = presentationFixture(target);
    const endpoint = `/api/realtime/devices/${target.id}/commands`;
    const commandRequests: unknown[] = [];
    const beforeInteractions = await interactionCount(request);

    // The recommendation is a presentation fixture. No mutating API call is
    // mocked: these assertions still inspect the real command and interaction stores.
    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );
    page.on('request', (message) => {
      if (message.method() === 'POST' && new URL(message.url()).pathname === endpoint) {
        commandRequests.push(message.postDataJSON());
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    const action = panel.getByTestId('suggested-action');
    await expect(action).toHaveCount(1);

    // Both approval rounds are explicitly exercised, but this test does not
    // also execute a command. That keeps cancellation independent of the
    // asynchronous command/Undo journey in the following test.
    for (let reviewNumber = 0; reviewNumber < 2; reviewNumber += 1) {
      await action.getByRole('button', { name: 'Review Relay On' }).click();
      await expect(
        action.getByRole('group', { name: 'Confirm recommended relay command' }),
      ).toBeVisible();
      await expect(action.getByRole('button', { name: 'Confirm Relay On' })).toBeVisible();
      await expect(action).toContainText(target.name);
      await action.getByRole('button', { name: 'Cancel' }).click();
      await expect(action.getByRole('button', { name: 'Review Relay On' })).toBeVisible();
    }

    expect(commandRequests).toHaveLength(0);
    expect(await interactionCount(request)).toBe(beforeInteractions);
    expect((await getCommandAudit(request, target.id)).totalAuditCount).toBe(0);
  });

  test('confirmation executes one real relay command and Undo restores the authoritative state', async ({
    page,
    request,
  }) => {
    const target = await registerOnlineActuator(request);
    const fixture = presentationFixture(target);
    const endpoint = `/api/realtime/devices/${target.id}/commands`;
    const commandRequests: { path: string; body: unknown }[] = [];
    const beforeInteractions = await interactionCount(request);

    // ONLY prediction GETs are mocked. The command POST, audit GET and Undo
    // POST always reach the running ASP.NET Core API.
    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );
    page.on('request', (message) => {
      if (message.method() === 'POST' && new URL(message.url()).pathname === endpoint) {
        commandRequests.push({
          path: new URL(message.url()).pathname,
          body: message.postDataJSON(),
        });
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    const action = panel.getByTestId('suggested-action');
    await expect(action).toHaveCount(1);

    await action.getByRole('button', { name: 'Review Relay On' }).click();
    await expect(
      action.getByRole('group', { name: 'Confirm recommended relay command' }),
    ).toBeVisible();
    await expect(action.getByRole('button', { name: 'Confirm Relay On' })).toBeEnabled();
    expect(commandRequests).toHaveLength(0);
    await action.getByRole('button', { name: 'Confirm Relay On' }).click();

    await expect(panel.getByTestId('suggested-action-result')).toContainText(
      'RelayOn accepted by server',
    );
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('On');
    await expect(page.getByTestId('realtime-undo-depth')).toHaveText('1');
    expect(commandRequests).toHaveLength(1);
    expect(commandRequests[0]).toEqual({ path: endpoint, body: { commandType: 'RelayOn' } });
    // A real accepted command produces exactly one CommandExecuted interaction.
    // Review and passive GETs do not add training evidence.
    expect(await interactionCount(request)).toBe(beforeInteractions + 1);

    const executed = await getCommandAudit(request, target.id);
    expect(executed).toMatchObject({
      totalAuditCount: 1,
      actuator: { commandedState: 'On', undoDepth: 1 },
    });

    // The existing manual command panel owns Undo and its real Stack snapshot.
    const undo = page.getByRole('button', { name: 'Undo latest command' });
    await expect(undo).toBeEnabled();
    await undo.click();
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
    await expect(page.getByTestId('realtime-undo-depth')).toHaveText('0');
    expect(await getCommandAudit(request, target.id)).toMatchObject({
      totalAuditCount: 2,
      actuator: { commandedState: 'Off', undoDepth: 0 },
    });
    expect(await interactionCount(request)).toBe(beforeInteractions + 2);
  });

  test('a suggestion withdrawn during revalidation never reaches the command API', async ({
    page,
    request,
  }) => {
    const target = await registerOnlineActuator(request);
    const fixture = presentationFixture(target);
    let stillEligible = true;
    let commandPosts = 0;

    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          ...fixture,
          recommendations: stillEligible ? fixture.recommendations : [],
        }),
      }),
    );
    page.on('request', (message) => {
      if (message.method() === 'POST' && message.url().endsWith(`/devices/${target.id}/commands`)) {
        commandPosts += 1;
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    await panel.getByRole('button', { name: 'Review Relay On' }).click();
    stillEligible = false;

    await panel.getByRole('button', { name: 'Confirm Relay On' }).click();
    await expect(panel.getByTestId('suggested-action-result')).toContainText('no longer eligible');
    await expect(panel.getByTestId('suggested-actions-empty')).toBeVisible();
    expect(commandPosts).toBe(0);
    expect((await getCommandAudit(request, target.id)).totalAuditCount).toBe(0);
  });

  test('server revalidation rejects an Offline target and preserves the command audit', async ({
    page,
    request,
  }) => {
    const target = await registerOnlineActuator(request);
    const fixture = presentationFixture(target);
    const endpoint = `/api/realtime/devices/${target.id}/commands`;

    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    await panel.getByRole('button', { name: 'Review Relay On' }).click();

    // Deliberately create a race between the displayed read-only prediction
    // and server device eligibility. Backend validation must still win.
    const disabled = await request.patch(`${apiBaseUrl}/api/sensors/${target.id}/status`, {
      data: { status: 'Offline' },
    });
    expect(disabled.status()).toBe(200);
    await expect(panel.getByRole('button', { name: 'Confirm Relay On' })).toBeEnabled();

    const rejectedResponse = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' && new URL(response.url()).pathname === endpoint,
    );
    await panel.getByRole('button', { name: 'Confirm Relay On' }).click();
    expect((await rejectedResponse).status()).toBe(409);

    await expect(panel.getByTestId('suggested-action-result')).toContainText(
      'rejected by the server',
    );
    expect((await getCommandAudit(request, target.id)).totalAuditCount).toBe(0);
  });

  test('failed recommendation revalidation does not issue a relay command', async ({
    page,
    request,
  }) => {
    const target = await registerOnlineActuator(request);
    const fixture = presentationFixture(target);
    let allowRecommendations = true;
    let commandPosts = 0;

    await page.route(recommendationsPath, (route) => {
      if (!allowRecommendations) return route.abort('failed');
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      });
    });
    page.on('request', (message) => {
      if (message.method() === 'POST' && message.url().includes(`/devices/${target.id}/commands`)) {
        commandPosts += 1;
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    await panel.getByRole('button', { name: 'Review Relay On' }).click();
    allowRecommendations = false;

    await panel.getByRole('button', { name: 'Confirm Relay On' }).click();
    await expect(panel.getByTestId('suggested-action-result')).toContainText(
      'Unable to revalidate this recommendation',
    );
    expect(commandPosts).toBe(0);
    expect((await getCommandAudit(request, target.id)).totalAuditCount).toBe(0);
  });
});
