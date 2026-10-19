import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import process from 'node:process';

import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test';

import type { OperatorInteractionHistoryResponse } from '../src/api/operatorInteractionsApi';
import type {
  PredictedRecommendationResponse,
  PredictedRecommendationsResponse,
} from '../src/api/recommendationsApi';
import type { SensorResponse } from '../src/api/sensorsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';
const evidenceDirectory = resolve(process.cwd(), '../../docs/evidence/part2');
const minuteMs = 60_000;

interface DashboardIssue {
  readonly id: string;
  readonly sensorProfileId: string;
  readonly type: string;
  readonly severity: string;
  readonly state: string;
  readonly detail: string;
}

interface DashboardResponse {
  readonly issues: readonly DashboardIssue[];
}

interface CommandAudit {
  readonly totalAuditCount: number;
  readonly actuator: {
    readonly commandedState: string;
    readonly undoDepth: number;
    readonly latestUndoableCommandId: string | null;
  };
}

/**
 * All HTTP operations in this suite reach the REAL .NET application.
 * No recommendations, interaction events, command results or active alerts
 * are mocked. A unique source/target pair isolates the simulated experiment
 * from other Playwright cases sharing the same process-local API.
 */
async function getJson<T>(request: APIRequestContext, endpoint: string): Promise<T> {
  const response = await request.get(`${apiBaseUrl}${endpoint}`);
  expect(response.status(), `GET ${endpoint} must succeed`).toBe(200);
  return (await response.json()) as T;
}

async function registerOnlineDevice(
  request: APIRequestContext,
  name: string,
  category: 'Environmental' | 'Actuator',
): Promise<SensorResponse> {
  const id = randomUUID();
  const response = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name: `${name} ${id}`,
      deviceIdentifier: `p210-journey-${id}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P2-10 Assessed Simulation',
        zone: 'Relative Moisture Change',
        nodeId: `Node-${id}`,
      },
      category,
      telemetryDataType: category === 'Environmental' ? 'Float' : 'Boolean',
    },
  });
  expect(response.status(), 'The fixture must create a real sensor').toBe(201);
  const device = (await response.json()) as SensorResponse;

  const online = await request.patch(`${apiBaseUrl}/api/sensors/${device.id}/status`, {
    data: { status: 'Online' },
  });
  expect(online.status(), 'The canonical registry must confirm Online').toBe(200);
  return device;
}

/**
 * Captured timestamps are deliberately historical, monotonically increasing
 * and separated by controlled intervals. The server's own policy still
 * classifies each new accepted reading; the browser never fabricates an issue.
 */
async function sendMoisture(
  request: APIRequestContext,
  sourceId: string,
  value: number,
  capturedAtMs: number,
): Promise<void> {
  const accepted = await request.post(`${apiBaseUrl}/api/sensors/${sourceId}/telemetry`, {
    data: {
      capturedAtUtc: new Date(capturedAtMs).toISOString(),
      floatValue: value,
      integerValue: null,
      booleanValue: null,
    },
  });
  expect(accepted.status(), `Float reading ${value} must be accepted`).toBe(201);
}

async function activeSpike(
  request: APIRequestContext,
  sourceId: string,
): Promise<DashboardIssue | undefined> {
  const current = await getJson<DashboardResponse>(request, '/api/dashboard/issues');
  return current.issues.find(
    (issue) =>
      issue.sensorProfileId === sourceId &&
      issue.type === 'TelemetrySpike' &&
      (issue.state === 'Active' || issue.state === 'Acknowledged'),
  );
}

async function waitForCriticalDrop(
  request: APIRequestContext,
  sourceId: string,
): Promise<DashboardIssue> {
  await expect
    .poll(async () => (await activeSpike(request, sourceId))?.severity, {
      message: 'The real relative-change classifier should create a critical episode',
      timeout: 15_000,
    })
    .toBe('Critical');

  const issue = await activeSpike(request, sourceId);
  expect(issue).toBeDefined();
  // Percent formatting can include a locale-specific space before '%'.
  expect(issue!.detail).toMatch(/80\s?% relative decrease/);
  return issue!;
}

async function interactions(
  request: APIRequestContext,
): Promise<OperatorInteractionHistoryResponse> {
  return getJson<OperatorInteractionHistoryResponse>(
    request,
    '/api/realtime/interactions?limit=200',
  );
}

async function audit(request: APIRequestContext, actuatorId: string): Promise<CommandAudit> {
  return getJson<CommandAudit>(request, `/api/realtime/devices/${actuatorId}/commands`);
}

async function recommendations(
  request: APIRequestContext,
): Promise<PredictedRecommendationsResponse> {
  return getJson<PredictedRecommendationsResponse>(request, '/api/realtime/recommendations');
}

function forPair(
  snapshot: PredictedRecommendationsResponse,
  sourceId: string,
  targetId: string,
): PredictedRecommendationResponse[] {
  return snapshot.recommendations.filter(
    (action) => action.sourceDeviceId === sourceId && action.targetActuatorId === targetId,
  );
}

/**
 * Search and alert viewing are deliberately performed through real React UI
 * controls, not POSTed as fabricated interaction-history records by the test.
 * This makes the exact P2-08 evidence chain visible to an assessor.
 */
async function viewSourceAlert(
  page: Page,
  request: APIRequestContext,
  source: SensorResponse,
  episodeId: string,
): Promise<void> {
  await page.getByRole('searchbox', { name: 'Name / deployment text' }).fill(source.name);
  await page.getByRole('combobox', { name: 'Category' }).selectOption('Environmental');
  await page.getByRole('button', { name: 'Search devices' }).click();

  const row = page.getByTestId('device-search-result');
  await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
  await expect(row).toContainText(source.name);
  await expect(row).toContainText('TelemetrySpike');
  await row.getByRole('button', { name: 'View active alert' }).click();

  await expect(page.getByText('Source alert selected')).toBeVisible();
  await expect
    .poll(async () => (await interactions(request)).events[0]?.alertEpisodeId, {
      message: 'The explicit AlertViewed receipt must refer to this real episode',
    })
    .toBe(episodeId);
  expect((await interactions(request)).events[0]).toMatchObject({
    kind: 'AlertViewed',
    deviceId: source.id,
    alertEpisodeId: episodeId,
  });
}

/** Selecting a searched actuator while an alert is active is a separate act. */
async function associateActuator(
  page: Page,
  request: APIRequestContext,
  target: SensorResponse,
  episodeId: string,
): Promise<void> {
  await page.getByRole('searchbox', { name: 'Name / deployment text' }).fill(target.name);
  await page.getByRole('combobox', { name: 'Category' }).selectOption('Actuator');
  await page.getByRole('button', { name: 'Search devices' }).click();

  await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
  const row = page.getByTestId('device-search-result');
  await expect(row).toContainText(target.name);
  await row.getByRole('button', { name: 'Associate with source alert' }).click();
  await expect(page.getByTestId('search-recorded-receipt')).toContainText('source alert episode');
  await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);

  expect((await interactions(request)).events[0]).toMatchObject({
    kind: 'SearchSubmitted',
    deviceId: target.id,
    alertEpisodeId: episodeId,
  });
}

/** The manual controls operate the production per-device Stack, not test state. */
async function manualRelayOnAndUndo(
  page: Page,
  request: APIRequestContext,
  targetId: string,
): Promise<void> {
  const on = page.getByRole('button', { name: 'Relay On', exact: true });
  await expect(on).toBeEnabled();
  await on.click();
  await expect(page.getByTestId('realtime-commanded-state')).toHaveText('On');
  await expect(page.getByTestId('realtime-undo-depth')).toHaveText('1');

  const undo = page.getByRole('button', { name: 'Undo latest command' });
  await expect(undo).toBeEnabled();

  // Listen before clicking so a swallowed control event cannot appear to
  // pass. Undo remains a SINGLE real request: never retry a mutation silently.
  const undoResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/realtime/devices/${targetId}/commands/undo`,
  );
  await undo.click();
  const undoResponse = await undoResponsePromise;
  expect(
    undoResponse.status(),
    `The real Undo endpoint must accept the current Stack command: ${await undoResponse.text()}`,
  ).toBe(200);

  // A server-confirmed reversal must be reflected both in the append-only
  // command audit and in the eventually refreshed operator UI.
  await expect
    .poll(async () => (await audit(request, targetId)).actuator.commandedState)
    .toBe('Off');
  await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
  await expect(page.getByTestId('realtime-undo-depth')).toHaveText('0');

  const state = await audit(request, targetId);
  expect(state.actuator).toMatchObject({ commandedState: 'Off', undoDepth: 0 });
  expect(state.actuator.latestUndoableCommandId).toBeNull();
}

/**
 * Real screenshot capture is opt-in: routine regression runs never create
 * binary artefacts or dirty the Git worktree. Only an actually learned panel
 * and a server-confirmed Undo state can produce the assessment screenshots.
 */
async function captureEvidence(element: Locator, filename: string): Promise<void> {
  if (process.env.SMARTX_CAPTURE_P210_EVIDENCE !== '1') return;
  mkdirSync(evidenceDirectory, { recursive: true });
  await element.screenshot({
    path: resolve(evidenceDirectory, filename),
    animations: 'disabled',
  });
}

test.describe('P2-10 genuine learn / suggest / execute / Undo journey', () => {
  // The server remains process-local for every Playwright worker. Even when a
  // browser assertion fails part-way through training, retire the source so
  // later tests cannot inherit an eligible recommendation from this fixture.
  let sourceToRetire: string | null = null;

  test.beforeEach(() => {
    sourceToRetire = null;
  });

  test.afterEach(async ({ request }) => {
    if (!sourceToRetire) return;
    const retired = await request.patch(`${apiBaseUrl}/api/sensors/${sourceToRetire}/status`, {
      data: { status: 'Decommissioned' },
    });
    expect(retired.status(), 'Cleanup must retire the real learning source').toBe(200);
    sourceToRetire = null;
  });

  test('three real moisture-drop episodes train a proactive suggestion before any fourth search', async ({
    page,
    request,
  }) => {
    // This is one bounded, real-HTTP multi-episode workflow rather than a
    // timing-dependent test relying on future real-world minutes elapsing.
    test.setTimeout(120_000);

    const source = await registerOnlineDevice(request, 'P210 Moisture Source', 'Environmental');
    sourceToRetire = source.id;
    const target = await registerOnlineDevice(request, 'P210 Irrigation Relay', 'Actuator');
    let captureAt = Date.now() - 45 * minuteMs;
    const episodeIds: string[] = [];

    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByText('SignalR connected')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('suggested-actions')).toBeVisible();

    // Initial baseline for a Float environmental sensor. "Moisture" describes
    // this fixture only; Smart-X does NOT implement a 20% absolute threshold.
    await sendMoisture(request, source.id, 50, captureAt);
    expect(forPair(await recommendations(request), source.id, target.id)).toHaveLength(0);

    for (let episode = 1; episode <= 3; episode += 1) {
      // 50 -> 10 in one captured minute is a critical 80% relative decrease.
      captureAt += minuteMs;
      await sendMoisture(request, source.id, 10, captureAt);
      const issue = await waitForCriticalDrop(request, source.id);
      expect(episodeIds).not.toContain(issue.id);
      episodeIds.push(issue.id);

      await viewSourceAlert(page, request, source, issue.id);
      await associateActuator(page, request, target, issue.id);
      await manualRelayOnAndUndo(page, request, target.id);
      expect((await audit(request, target.id)).totalAuditCount).toBe(2 * episode);

      // Two consecutive stable comparisons resolve this exact HashSet member.
      // Repeated clicks while it remained active would NOT train new support.
      captureAt += 30_000;
      await sendMoisture(request, source.id, 10, captureAt);
      captureAt += 30_000;
      await sendMoisture(request, source.id, 10, captureAt);
      await expect
        .poll(async () => activeSpike(request, source.id), {
          message: 'Stable readings must genuinely resolve the previous episode',
        })
        .toBeUndefined();

      // Re-establish 50 after a >5-minute captured-time gap. This is a stable
      // baseline transition under the existing maximum spike interval, so a
      // new 50 -> 10 drop starts a fresh *decrease* episode next iteration.
      captureAt += 6 * minuteMs;
      await sendMoisture(request, source.id, 50, captureAt);
      expect(await activeSpike(request, source.id)).toBeUndefined();
    }

    expect(new Set(episodeIds).size).toBe(3);
    // Inspect the actual append-only interaction ledger, not a test counter.
    // Each supporting episode must have its own explicit view and association;
    // every genuine relay transition and Undo has a separate server event.
    const trainingEvents = (await interactions(request)).events;
    const sourceViews = trainingEvents.filter(
      (event) => event.kind === 'AlertViewed' && event.deviceId === source.id,
    );
    const associations = trainingEvents.filter(
      (event) =>
        event.kind === 'SearchSubmitted' &&
        event.deviceId === target.id &&
        event.alertEpisodeId !== null,
    );
    const acceptedCommands = trainingEvents.filter(
      (event) => event.kind === 'CommandExecuted' && event.deviceId === target.id,
    );
    const undoneCommands = trainingEvents.filter(
      (event) => event.kind === 'CommandUndone' && event.deviceId === target.id,
    );
    expect(sourceViews.map((event) => event.alertEpisodeId).sort()).toEqual([...episodeIds].sort());
    expect(associations.map((event) => event.alertEpisodeId).sort()).toEqual(
      [...episodeIds].sort(),
    );
    expect(acceptedCommands).toHaveLength(3);
    expect(undoneCommands).toHaveLength(3);
    expect(forPair(await recommendations(request), source.id, target.id)).toHaveLength(0);
    expect((await audit(request, target.id)).totalAuditCount).toBe(6);
    const beforeProactiveView = (await interactions(request)).totalRecorded;

    // Fourth episode is intentionally NOT viewed, searched or associated.
    // A learned action must appear proactively from the first three genuine
    // alert/view/search/successful-command episodes.
    captureAt += minuteMs;
    await sendMoisture(request, source.id, 10, captureAt);
    const fourth = await waitForCriticalDrop(request, source.id);
    expect(episodeIds).not.toContain(fourth.id);

    await expect
      .poll(async () => forPair(await recommendations(request), source.id, target.id), {
        message: 'Three genuine episodes should qualify a new active decrease',
        timeout: 15_000,
      })
      .toHaveLength(1);
    const learned = forPair(await recommendations(request), source.id, target.id)[0];
    expect(learned).toMatchObject({
      sourceAlertEpisodeId: fourth.id,
      conditionType: 'TelemetrySpike',
      severity: 'Critical',
      movement: 'Decrease',
      commandType: 'RelayOn',
      supportingEpisodes: 3,
      confidenceBand: 'High',
    });
    expect(learned.score).toBe(85);
    expect(learned.explanation).toContain('3 distinct alert episodes');
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView);

    const panel = page.getByTestId('suggested-actions');
    const action = panel.getByTestId('suggested-action').filter({ hasText: target.name });
    await expect(action).toHaveCount(1, { timeout: 20_000 });
    await expect(action).toContainText('3 distinct episodes');
    await expect(action).toContainText('High confidence / 85 of 100');
    await expect(action).toContainText(source.name);
    await expect(action).toContainText(learned.explanation);
    // No new deliberate interaction was created by HTTP/SignalR or the panel.
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView);

    // Navigation preserves the actual selected actuator and submitted search
    // criteria, without creating additional DeviceViewed/SearchSubmitted events.
    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);
    await expect(page.getByRole('searchbox', { name: 'Name / deployment text' })).toHaveValue(
      target.name,
    );
    await expect(action).toHaveCount(1, { timeout: 15_000 });
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView);

    // Keyboard-only approval review and cancellation are passive: neither
    // press sends a command or creates a synthetic operator evidence record.
    const review = action.getByRole('button', { name: 'Review Relay On' });
    await review.focus();
    await page.keyboard.press('Enter');
    await expect(
      action.getByRole('group', { name: 'Confirm recommended relay command' }),
    ).toBeVisible();
    const cancel = action.getByRole('button', { name: 'Cancel' });
    await cancel.focus();
    await page.keyboard.press('Enter');
    await expect(
      action.getByRole('group', { name: 'Confirm recommended relay command' }),
    ).toHaveCount(0);
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView);
    expect((await audit(request, target.id)).totalAuditCount).toBe(6);

    // The real explanation, evidence, review control and approval remain
    // usable on a narrow mobile viewport without overflowing the document.
    await page.setViewportSize({ width: 375, height: 812 });
    await expect(action.getByRole('button', { name: 'Review Relay On' })).toBeVisible();
    const pageWidth = await page.evaluate(() => ({
      content: document.documentElement.scrollWidth,
      viewport: window.innerWidth,
    }));
    expect(pageWidth.content).toBeLessThanOrEqual(pageWidth.viewport + 1);
    await page.setViewportSize({ width: 1440, height: 900 });
    await captureEvidence(panel, '08-suggested-actions.png');

    // Record only actual browser-issued command requests after the fourth
    // condition appears; the three earlier manual training cycles are done.
    const posts: { path: string; commandType: string }[] = [];
    page.on('request', (sent) => {
      if (
        sent.method() === 'POST' &&
        new URL(sent.url()).pathname === `/api/realtime/devices/${target.id}/commands`
      ) {
        const body = sent.postDataJSON() as { commandType: string };
        posts.push({ path: new URL(sent.url()).pathname, commandType: body.commandType });
      }
    });

    await action.getByRole('button', { name: 'Review Relay On' }).click();
    await action.getByRole('button', { name: 'Confirm Relay On' }).click();
    await expect(panel.getByTestId('suggested-action-result')).toContainText(
      'RelayOn accepted by server',
    );
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('On');
    await expect(page.getByTestId('realtime-undo-depth')).toHaveText('1');
    expect(posts).toEqual([
      { path: `/api/realtime/devices/${target.id}/commands`, commandType: 'RelayOn' },
    ]);
    expect((await audit(request, target.id)).totalAuditCount).toBe(7);
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView + 1);

    // The same manual Undo control uses the server Stack's expected latest ID.
    const undo = page.getByRole('button', { name: 'Undo latest command' });
    await expect(undo).toBeEnabled();
    const undoResponsePromise = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname === `/api/realtime/devices/${target.id}/commands/undo`,
    );
    await undo.click();
    const undoResponse = await undoResponsePromise;
    expect(
      undoResponse.status(),
      `The accepted recommendation must be reversible: ${await undoResponse.text()}`,
    ).toBe(200);
    await expect
      .poll(async () => (await audit(request, target.id)).actuator.commandedState)
      .toBe('Off');
    await expect(page.getByTestId('realtime-commanded-state')).toHaveText('Off');
    await expect(page.getByTestId('realtime-undo-depth')).toHaveText('0');
    expect((await audit(request, target.id)).totalAuditCount).toBe(8);
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView + 2);
    await captureEvidence(
      page.locator('[aria-labelledby="realtime-command-title"]'),
      '09-suggested-action-undo.png',
    );

    // Leave the process-local server without a qualifying active source issue:
    // later read-only tests must not inherit a live recommendation from us.
    captureAt += 30_000;
    await sendMoisture(request, source.id, 10, captureAt);
    captureAt += 30_000;
    await sendMoisture(request, source.id, 10, captureAt);
    await expect.poll(async () => activeSpike(request, source.id)).toBeUndefined();
    await expect
      .poll(async () => forPair(await recommendations(request), source.id, target.id))
      .toHaveLength(0);
    await expect(action).toHaveCount(0, { timeout: 15_000 });
    expect((await interactions(request)).totalRecorded).toBe(beforeProactiveView + 2);
  });

  test('after a genuine SignalR reconnect the panel re-reads the real recommendation API', async ({
    page,
  }) => {
    let recommendationReads = 0;
    page.on('request', (sent) => {
      if (
        sent.method() === 'GET' &&
        new URL(sent.url()).pathname === '/api/realtime/recommendations'
      ) {
        recommendationReads += 1;
      }
    });

    // The API itself remains available. Only the hub negotiation is blocked,
    // so the real UI must use its existing retry/reconnect and HTTP fallback.
    const negotiate = '**/hubs/realtime/negotiate**';
    await page.route(negotiate, (route) => route.abort('failed'));
    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    await expect(page.getByText('SignalR disconnected')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('suggested-actions')).toBeVisible();
    await expect.poll(() => recommendationReads).toBeGreaterThan(0);

    const beforeReconnect = recommendationReads;
    await page.unroute(negotiate);
    await expect(page.getByText('SignalR connected')).toBeVisible({ timeout: 20_000 });
    await expect
      .poll(() => recommendationReads, {
        message: 'A real connection retry must invalidate the HTTP guidance snapshot',
        timeout: 15_000,
      })
      .toBeGreaterThan(beforeReconnect);
    await expect(page.getByTestId('suggested-actions')).toBeVisible();
  });
});
