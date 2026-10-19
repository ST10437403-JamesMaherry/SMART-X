import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

import type { PredictedRecommendationsResponse } from '../src/api/recommendationsApi';
import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';
const recommendationsPath = '**/api/realtime/recommendations';

/**
 * The transport fixture supplies a suggested-action PRESENTATION only.
 * Successful relay commands and Stack-backed Undo go to the REAL .NET API.
 * The separate full-journey suite proves genuine learning against the real API.
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
    // Hold one deliberately requested PASSIVE GET so we can prove that an
    // in-progress refresh cannot swallow the Review or Confirm controls.
    let holdNextRead = false;
    let releaseHeldRead: (() => void) | undefined;
    let signalReadStarted: (() => void) | undefined;
    const heldReadStarted = new Promise<void>((resolve) => {
      signalReadStarted = resolve;
    });

    await page.route(recommendationsPath, async (route) => {
      if (holdNextRead) {
        holdNextRead = false;
        await new Promise<void>((resolve) => {
          releaseHeldRead = resolve;
          signalReadStarted?.();
        });
      }

      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      });
    });
    page.on('request', (message) => {
      if (message.method() === 'POST' && new URL(message.url()).pathname === endpoint) {
        commandRequests.push(message.postDataJSON());
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    const action = panel.getByTestId('suggested-action');
    await expect(action).toHaveCount(1);

    const review = action.getByRole('button', { name: 'Review Relay On' });
    const confirmation = action.getByRole('group', {
      name: 'Confirm recommended relay command',
    });
    const confirm = action.getByRole('button', { name: 'Confirm Relay On' });
    const refresh = panel.getByRole('button', { name: 'Refresh suggestions' });

    // Wait for the first authoritative snapshot before starting an intentional
    // background refresh. A pending GET should never disable deliberate Review.
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(refresh).toBeEnabled();
    holdNextRead = true;
    await refresh.click();

    try {
      await heldReadStarted;
      await expect(panel.getByRole('button', { name: 'Checking...' })).toBeVisible();
      await expect(review).toBeEnabled();
      await review.click();
      await expect(confirmation).toBeVisible();
      await expect(confirm).toBeEnabled();
      await expect(action).toContainText(target.name);
    } finally {
      // Never leave an intercepted GET hanging when an assertion fails.
      releaseHeldRead?.();
    }

    // Completing the passive refresh must retain the open confirmation
    // because the server supplied the same recommendation identity.
    await expect(refresh).toBeEnabled();
    await expect(confirmation).toBeVisible();
    await action.getByRole('button', { name: 'Cancel' }).click();
    await expect(review).toBeVisible();

    // A second independent review/cancel cycle is still passive.
    await expect(review).toBeEnabled();
    await review.click();
    await expect(confirmation).toBeVisible();
    await expect(confirm).toBeEnabled();
    await action.getByRole('button', { name: 'Cancel' }).click();
    await expect(review).toBeVisible();

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
    let commandPosts = 0;
    const beforeInteractions = await interactionCount(request);

    // Passive recommendation refreshes always pass an AbortSignal; the explicit
    // operator-confirmation GET does not. Simulate an alert recovering EXACTLY
    // when that confirmation GET occurs, not earlier while Playwright is
    // locating the button. This prevents a background SignalR refresh from
    // legitimately removing the button before the attempted operator action.
    //
    // The response stays withdrawn for all subsequent GETs, including passive
    // refreshes. No production API or command pathway is replaced.
    await page.addInitScript(
      (withdrawnSnapshot) => {
        const nativeFetch = window.fetch.bind(window);
        let withdrawn = false;

        window.fetch = (input, init) => {
          const requestUrl = input instanceof Request ? input.url : String(input);
          const path = new URL(requestUrl, window.location.href).pathname;

          if (path === '/api/realtime/recommendations') {
            if (!init?.signal) withdrawn = true;

            if (withdrawn) {
              return Promise.resolve(
                new Response(JSON.stringify(withdrawnSnapshot), {
                  status: 200,
                  headers: { 'Content-Type': 'application/json' },
                }),
              );
            }
          }

          return nativeFetch(input, init);
        };
      },
      { ...fixture, recommendations: [] },
    );

    // Before explicit confirmation, passive GETs still show the same stable
    // recommendation. After confirmation, the revalidation GET withdraws it.
    await page.route(recommendationsPath, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(fixture),
      }),
    );
    page.on('request', (message) => {
      if (message.method() === 'POST' && message.url().endsWith(`/devices/${target.id}/commands`)) {
        commandPosts += 1;
      }
    });

    await page.goto('/realtime');
    const panel = page.getByTestId('suggested-actions');
    await expect(panel.getByTestId('suggested-action')).toHaveCount(1);
    await panel.getByRole('button', { name: 'Review Relay On' }).click();
    await expect(
      panel.getByRole('group', { name: 'Confirm recommended relay command' }),
    ).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Confirm Relay On' })).toBeEnabled();

    await panel.getByRole('button', { name: 'Confirm Relay On' }).click();
    await expect(panel.getByTestId('suggested-action-result')).toContainText('no longer eligible');
    await expect(panel.getByTestId('suggested-actions-empty')).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Confirm Relay On' })).toHaveCount(0);

    // A withdrawn suggestion must never cause a relay POST, command audit
    // entry, or synthetic pattern-learning evidence.
    expect(commandPosts).toBe(0);
    expect((await getCommandAudit(request, target.id)).totalAuditCount).toBe(0);
    expect(await interactionCount(request)).toBe(beforeInteractions);
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
