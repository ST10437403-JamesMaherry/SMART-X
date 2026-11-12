import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext, type Locator } from '@playwright/test';

import type { SensorResponse } from '../src/api/sensorsApi';
import type { MeshTopologyResponse } from '../src/api/topologyApi';
import type {
  TopologyInsightResponse,
  TopologyInsightsResponse,
} from '../src/api/topologyInsightsApi';
import type { OperatorInteractionHistoryResponse } from '../src/api/operatorInteractionsApi';

const apiBaseUrl = 'http://127.0.0.1:5095';
const insightsPath = `${apiBaseUrl}/api/topology/insights`;

// The API returns up to 16 affected IDs, while the UI deliberately previews only five.
const affectedPreviewLimit = 5;

type TestNetwork = {
  readonly relay: SensorResponse;
  readonly leaf: SensorResponse;
};

/** Create real registry devices, not browser-only fixtures or fake UUIDs. */
async function registerOnline(request: APIRequestContext, name: string): Promise<SensorResponse> {
  const id = randomUUID();
  const created = await request.post(`${apiBaseUrl}/api/sensors`, {
    data: {
      name,
      deviceIdentifier: `p310-${id}`,
      identifierKind: 'UniqueIdentifier',
      deploymentLocation: {
        facility: 'P3-10 Proactive Insight Test',
        zone: 'Real HTTP Failure Scenario',
        nodeId: id,
      },
      category: 'Environmental',
      telemetryDataType: 'Float',
    },
  });
  expect(created.status()).toBe(201);
  const device = (await created.json()) as SensorResponse;
  const online = await request.patch(`${apiBaseUrl}/api/sensors/${device.id}/status`, {
    data: { status: 'Online' },
  });
  expect(online.status()).toBe(200);
  return device;
}

async function topology(request: APIRequestContext): Promise<MeshTopologyResponse> {
  const response = await request.get(`${apiBaseUrl}/api/topology`);
  expect(response.status()).toBe(200);
  return (await response.json()) as MeshTopologyResponse;
}

async function insights(request: APIRequestContext): Promise<TopologyInsightsResponse> {
  const response = await request.get(insightsPath);
  expect(response.status()).toBe(200);
  expect(response.headers()['cache-control']).toContain('no-store');
  return (await response.json()) as TopologyInsightsResponse;
}

async function interactions(
  request: APIRequestContext,
): Promise<OperatorInteractionHistoryResponse> {
  const response = await request.get(`${apiBaseUrl}/api/realtime/interactions?limit=50`);
  expect(response.status()).toBe(200);
  return (await response.json()) as OperatorInteractionHistoryResponse;
}

async function status(
  request: APIRequestContext,
  device: SensorResponse,
  value: 'Online' | 'Offline' | 'Maintenance',
): Promise<void> {
  const response = await request.patch(`${apiBaseUrl}/api/sensors/${device.id}/status`, {
    data: { status: value },
  });
  expect(response.status()).toBe(200);
}

/** One distinct registered gateway -> relay -> leaf logical mesh. */
async function createNetwork(request: APIRequestContext): Promise<TestNetwork> {
  const prefix = `P3-10 Insight ${randomUUID()}`;
  const relay = await registerOnline(request, `${prefix} Relay`);
  const leaf = await registerOnline(request, `${prefix} Leaf`);
  const snapshot = await topology(request);
  const relayKey = snapshot.nodes.find((node) => node.sensorProfileId === relay.id)?.key;
  const leafKey = snapshot.nodes.find((node) => node.sensorProfileId === leaf.id)?.key;
  expect(relayKey).toBeTruthy();
  expect(leafKey).toBeTruthy();
  for (const [a, b] of [
    ['gateway', relayKey!],
    [relayKey!, leafKey!],
  ]) {
    const link = await request.post(`${apiBaseUrl}/api/topology/links`, {
      data: { nodeAKey: a, nodeBKey: b, simulatedEnergyCost: 1.25 },
    });
    expect(link.status()).toBe(201);
  }
  return { relay, leaf };
}

/** A specific device route is authoritative even if the bounded insight ID list omits it. */
async function deviceRouteStatus(request: APIRequestContext, deviceId: string): Promise<string> {
  const response = await request.get(`${apiBaseUrl}/api/topology/devices/${deviceId}/route`);
  expect(response.status()).toBe(200);
  const route = (await response.json()) as { status: string };
  return route.status;
}

/**
 * Verify the precise bounded subset displayed by the UI, not the wider set
 * returned by the API. Earlier real-API tests retain registered sensors in
 * this process, so a newly created leaf is not guaranteed to be among the
 * first five IDs even when it is present in the API's first sixteen.
 *
 * Every displayed identity must be in the authoritative graph at the same
 * revision and must retain its genuine registered name. This complements the
 * independent per-device BFS route check performed in the test below.
 */
async function expectRegisteredAffectedPreview(
  panel: Locator,
  insight: TopologyInsightResponse,
  graph: MeshTopologyResponse,
): Promise<void> {
  expect(insight.evidenceGraphVersion).toBe(graph.graphVersion);
  expect(insight.affectedSensorIds.length).toBeLessThanOrEqual(16);
  expect(insight.affectedRegisteredSensorCount).toBe(
    insight.affectedSensorIds.length + insight.additionalAffectedSensorCount,
  );

  const previewIds = insight.affectedSensorIds.slice(0, affectedPreviewLimit);
  expect(previewIds.length).toBeGreaterThan(0);
  const rows = panel.getByTestId('insight-affected-sensor');
  await expect(rows).toHaveCount(previewIds.length);
  await expect(panel).toContainText(
    `Affected registered devices: ${insight.affectedRegisteredSensorCount}`,
  );

  for (const [index, id] of previewIds.entries()) {
    const registered = graph.nodes.find(
      (node) => node.kind === 'Sensor' && node.sensorProfileId?.toLowerCase() === id.toLowerCase(),
    );
    expect(registered?.name, `Affected sensor ${id} must exist in the current graph`).toBeTruthy();

    // The order, identity and display name must all come from the real API.
    await expect(rows.nth(index)).toHaveAttribute('data-sensor-id', id);
    await expect(rows.nth(index)).toContainText(registered!.name!);
  }

  if (insight.affectedRegisteredSensorCount > affectedPreviewLimit) {
    await expect(panel).toContainText(
      `Showing up to ${affectedPreviewLimit} real device identities here.`,
    );
  }
}

test.describe('P3-10 proactive network insights and evidence separation', () => {
  test('shows an immediate real disconnection in topology and operations without any manual search', async ({
    request,
    page,
  }) => {
    const { relay, leaf } = await createNetwork(request);
    const before = await interactions(request);
    await page.goto('/topology');
    await expect(page.getByTestId('topology-insights')).toBeVisible();
    await expect(
      page.getByTestId('topology-insights').getByText(/Rule-derived from current gateway/),
    ).toBeVisible();
    expect(await deviceRouteStatus(request, leaf.id)).toBe('Reachable');

    const commandPosts: string[] = [];
    page.on('request', (browserRequest) => {
      if (
        browserRequest.method() === 'POST' &&
        /\/api\/realtime\/devices\/[^/]+\/commands/.test(browserRequest.url())
      ) {
        commandPosts.push(browserRequest.url());
      }
    });

    await status(request, relay, 'Offline');
    expect(await deviceRouteStatus(request, leaf.id)).toBe('Disconnected');
    const changedVersion = (await topology(request)).graphVersion;
    await expect(
      page
        .getByTestId('topology-insights')
        .getByText(`Revision ${changedVersion}`, { exact: true }),
    ).toBeVisible({ timeout: 30_000 });
    await expect(
      page
        .getByTestId('topology-insights')
        .locator('[data-insight-key="topology:gateway-disconnected"]'),
    ).toContainText('Gateway route unavailable', { timeout: 30_000 });
    const disconnected = page
      .getByTestId('topology-insights')
      .locator('[data-insight-key="topology:gateway-disconnected"]');
    await expect(disconnected).toHaveAttribute('data-current', 'true');
    await expect(disconnected).toContainText('Rule-derived · Not learned');
    await expect(disconnected).toContainText('CurrentTopologyBfs');
    const actual = await insights(request);
    const graphEvidence = await topology(request);
    expect(actual.graphVersion).toBe(changedVersion);
    expect(graphEvidence.graphVersion).toBe(changedVersion);
    const unreachable = actual.insights.find((item) => item.kind === 'GatewayDisconnected');
    expect(unreachable).toBeDefined();
    await expectRegisteredAffectedPreview(disconnected, unreachable!, graphEvidence);
    // The leaf itself is proven disconnected by the real BFS route endpoint.
    // The global warning may preview other registered devices from prior tests.
    expect(actual.insights.every((item) => item.isLearned === false)).toBe(true);
    const afterTopology = await interactions(request);
    expect(afterTopology.totalRecorded).toBe(before.totalRecorded);
    expect(commandPosts).toEqual([]);

    // Operations has no preloaded graph: it must read a matching revision
    // rather than guessing the name of the affected UUID.
    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    const operationsPanel = page.getByTestId('topology-insights');
    await expect(
      operationsPanel.getByText(`Revision ${changedVersion}`, { exact: true }),
    ).toBeVisible({ timeout: 30_000 });
    const operationsDisconnected = operationsPanel.locator(
      '[data-insight-key="topology:gateway-disconnected"]',
    );
    await expect(operationsDisconnected).toContainText('Gateway route unavailable', {
      timeout: 30_000,
    });
    await expectRegisteredAffectedPreview(operationsDisconnected, unreachable!, graphEvidence);
    await expect(page.getByRole('heading', { name: 'Suggested Actions' })).toBeVisible();
    await expect(page.getByTestId('suggested-actions')).toContainText('LEARNED GUIDANCE');
    expect((await interactions(request)).totalRecorded).toBe(before.totalRecorded);
    expect(commandPosts).toEqual([]);
  });

  test('deduplicates unchanged graph reads and removes stale active warnings after genuine recovery', async ({
    request,
    page,
  }) => {
    const { relay, leaf } = await createNetwork(request);
    await page.goto('/topology');
    await page.getByLabel('Select a registered sensor').selectOption(leaf.id);
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);

    await status(request, relay, 'Maintenance');
    const first = await insights(request);
    const second = await insights(request);
    const firstDisconnected = first.insights.find((item) => item.kind === 'GatewayDisconnected');
    const secondDisconnected = second.insights.find((item) => item.key === firstDisconnected?.key);
    expect(firstDisconnected).toBeDefined();
    expect(secondDisconnected?.firstObservedAtUtc).toBe(firstDisconnected?.firstObservedAtUtc);
    expect(second.projectionComputedAtUtc).toBe(first.projectionComputedAtUtc);
    expect(second.graphVersion).toBe(first.graphVersion);
    expect(second.insights.filter((item) => item.key === firstDisconnected?.key)).toHaveLength(1);

    expect(await deviceRouteStatus(request, leaf.id)).toBe('Disconnected');
    await expect(
      page
        .getByTestId('topology-insights')
        .getByText(`Revision ${first.graphVersion}`, { exact: true }),
    ).toBeVisible({ timeout: 30_000 });

    await status(request, relay, 'Online');
    await expect
      .poll(async () => deviceRouteStatus(request, leaf.id), { timeout: 30_000 })
      .toBe('Reachable');
    const restoredVersion = (await topology(request)).graphVersion;
    await expect(
      page
        .getByTestId('topology-insights')
        .getByText(`Revision ${restoredVersion}`, { exact: true }),
    ).toBeVisible({ timeout: 30_000 });
    await expect(page.getByLabel('Select a registered sensor')).toHaveValue(leaf.id);
    // Older, independent disconnected fixtures may keep a global warning
    // active. The selected leaf's real route, not an empty whole-network list,
    // proves this restoration.

    // Recovery is transient and rate-limited globally. It can be absent if
    // another test already exercised the cooldown; never demand a fake event.
    const recovered = await insights(request);
    const notice = recovered.insights.find((item) => item.kind === 'GatewayConnectivityRestored');
    if (notice) {
      expect(notice.isCurrent).toBe(false);
      expect(notice.expiresAtUtc).not.toBeNull();
    }

    // Evidence contains measured response bytes, not a claimed benchmark.
    const measuredResponse = await request.get(insightsPath);
    expect(measuredResponse.status()).toBe(200);
    const measuredBytes = (await measuredResponse.body()).byteLength;
    expect(measuredBytes).toBeGreaterThan(0);
    await test.info().attach('p3-10-live-insights-http-evidence.json', {
      body: Buffer.from(
        JSON.stringify(
          {
            graphVersion: recovered.graphVersion,
            projectionComputedAtUtc: recovered.projectionComputedAtUtc,
            insightCount: recovered.insights.length,
            responseBytes: measuredBytes,
            observedAtUtc: new Date().toISOString(),
          },
          null,
          2,
        ),
      ),
      contentType: 'application/json',
    });
  });

  test('presents genuine submitted search evidence without retraining on a passive read', async ({
    request,
    page,
  }) => {
    const device = await registerOnline(request, `P3-10 Search ${randomUUID()}`);
    await page.goto('/realtime');
    await expect(page.getByText('HTTP snapshot ready')).toBeVisible();
    const insightsPanel = page.getByTestId('topology-insights');
    await expect(insightsPanel.getByText(/Authoritative graph/)).toBeVisible();
    await expect(page.getByText('SignalR connected')).toBeVisible();
    // Drain the existing 150 ms initial hub-invalidation window before
    // counting search-triggered requests; startup is not the measured action.
    await page.waitForTimeout(250);

    let insightReads = 0;
    page.on('request', (browserRequest) => {
      if (
        browserRequest.method() === 'GET' &&
        browserRequest.url().endsWith('/api/topology/insights')
      ) {
        insightReads += 1;
      }
    });
    const before = await interactions(request);
    await page.getByRole('searchbox', { name: 'Name / deployment text' }).fill(device.name);
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('SearchSubmitted');
    await expect
      .poll(async () => (await interactions(request)).totalRecorded)
      .toBe(before.totalRecorded + 1);

    const evidence = page.getByTestId('operator-searches');
    await evidence.getByRole('button', { name: 'Refresh search evidence' }).click();
    await expect(
      evidence.getByTestId('operator-search-event').filter({ hasText: device.name }),
    ).toBeVisible({ timeout: 30_000 });
    await expect(evidence).toContainText('submission(s)');
    const after = await interactions(request);
    expect(
      after.events.some(
        (entry) => entry.kind === 'SearchSubmitted' && entry.searchText === device.name,
      ),
    ).toBe(true);

    const readsAfterSearch = insightReads;
    await evidence.getByRole('button', { name: 'Refresh search evidence' }).click();
    await expect(
      evidence.getByTestId('operator-search-event').filter({ hasText: device.name }),
    ).toBeVisible();
    expect((await interactions(request)).totalRecorded).toBe(after.totalRecorded);
    // Search invalidations must not trigger a whole graph/BFS read.
    expect(insightReads).toBe(readsAfterSearch);
  });

  test('keeps mobile risk evidence readable and recovers safely after insights GET failure', async ({
    request,
    page,
  }) => {
    const { relay, leaf } = await createNetwork(request);
    await status(request, relay, 'Offline');
    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/topology');
    await expect(
      page
        .getByTestId('topology-insights')
        .locator('[data-insight-key="topology:gateway-disconnected"]'),
    ).toContainText('Gateway route unavailable');
    expect(await deviceRouteStatus(request, leaf.id)).toBe('Disconnected');
    const documentOverflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(documentOverflow).toBeLessThanOrEqual(1);

    await page.route('**/api/topology/insights', (route) => route.abort('failed'));
    await page.goto('/realtime');
    const panel = page.getByTestId('topology-insights');
    await expect(panel.getByRole('alert')).toContainText('No stale alerts are displayed');
    await expect(panel.getByTestId('topology-insight')).toHaveCount(0);
    await page.unroute('**/api/topology/insights');
    await panel.getByRole('button', { name: 'Retry live insight request' }).click();
    await expect(panel.locator('[data-insight-key="topology:gateway-disconnected"]')).toContainText(
      'Gateway route unavailable',
      { timeout: 30_000 },
    );
    expect(await deviceRouteStatus(request, leaf.id)).toBe('Disconnected');
    const overflowAfterRecovery = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflowAfterRecovery).toBeLessThanOrEqual(1);
  });
});
