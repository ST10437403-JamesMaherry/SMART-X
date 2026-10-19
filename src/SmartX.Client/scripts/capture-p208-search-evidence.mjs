/**
 * PROG7312 / Smart-X P2-08 — reproducible search and operator-history evidence.
 *
 * Usage (from src/SmartX.Client):
 *   node scripts/capture-p208-search-evidence.mjs
 *   node scripts/capture-p208-search-evidence.mjs --base-url http://localhost:9090
 *
 * This is NOT a mock UI or a fixture generator. Chromium interacts with the
 * running React application; independent HTTP reads verify the real backend.
 * Every screenshot comes directly from Playwright's browser screenshot API.
 *
 * Safety:
 * - Only localhost HTTP targets are accepted. This script creates sensors.
 * - Random demonstration identifiers prevent existing-device collisions.
 * - No encryption keys, tokens, cookies, or response headers are exported.
 * - Evidence is staged outside the repository and published only after all
 *   assertions pass. On failure, previous committed evidence is preserved.
 * - The process-local interaction store should be quiet during capture.
 */

import { randomBytes } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { chromium, expect, request as playwrightRequest } from '@playwright/test';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '../../..');
const evidenceDirectory = path.join(repositoryRoot, 'docs/evidence/part2');
const waitMs = 20_000;

function assert(condition, message) {
  if (!condition) throw new Error(`P2-08 evidence validation failed: ${message}`);
}

function readOptions(argv) {
  let baseUrl = 'http://localhost:8080';
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') {
      console.log(
        'Usage: node scripts/capture-p208-search-evidence.mjs [--base-url http://localhost:8080]',
      );
      process.exit(0);
    }
    if (argument !== '--base-url' || index + 1 >= argv.length) {
      throw new Error(`Unknown or incomplete argument: ${argument}`);
    }
    baseUrl = argv[++index];
  }
  const address = new URL(baseUrl);
  assert(
    address.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(address.hostname),
    '--base-url must use HTTP localhost or 127.0.0.1 (never a remote deployment)',
  );
  assert(
    address.pathname === '/' && !address.search && !address.hash,
    'provide an origin, not a path',
  );
  return address.origin;
}

/** Use explicit expected HTTP status so a ProblemDetails response never passes. */
async function jsonRequest(request, method, route, expectedStatus = 200, data) {
  const response = await request[method](route, data === undefined ? {} : { data });
  const responseText = await response.text();
  assert(
    response.status() === expectedStatus,
    `${method.toUpperCase()} ${route} expected HTTP ${expectedStatus}, got ${response.status()}: ${responseText.slice(0, 350)}`,
  );
  try {
    return JSON.parse(responseText);
  } catch {
    throw new Error(`${route} returned non-JSON content`);
  }
}

async function readHistory(request) {
  const history = await jsonRequest(request, 'get', '/api/realtime/interactions?limit=200');
  assert(Number.isInteger(history.totalRecorded), 'interaction totalRecorded must be an integer');
  assert(Array.isArray(history.events), 'interaction history must include real events');
  assert(
    history.capacity === 2000,
    'expected the configured 2,000-entry bounded interaction store',
  );
  return history;
}

async function readSearch(request, parameters) {
  const query = new URLSearchParams({ ...parameters, limit: '25', offset: '0' });
  return jsonRequest(request, 'get', `/api/realtime/devices/search?${query.toString()}`);
}

function assertSingleResult(result, deviceId, reason) {
  assert(
    result.totalMatches === 1 && result.devices.length === 1,
    `${reason}: expected one matching device`,
  );
  assert(result.devices[0].device.id === deviceId, `${reason}: wrong device returned by query`);
}

/** Give every screenshot run an independent device namespace. */
async function registerDevice(request, name, kind, category, telemetryType, identifier) {
  return jsonRequest(request, 'post', '/api/sensors', 201, {
    name,
    deviceIdentifier: identifier,
    identifierKind: kind,
    deploymentLocation: {
      facility: 'Smart-X Evidence Lab',
      zone: 'P2-08 Search and Prediction',
      nodeId: `Node-${randomBytes(6).toString('hex')}`,
    },
    category,
    telemetryDataType: telemetryType,
  });
}

async function setDeviceStatus(request, id, status) {
  await jsonRequest(request, 'patch', `/api/sensors/${encodeURIComponent(id)}/status`, 200, {
    status,
  });
}

function requireEvent(event, kind, expected) {
  assert(event?.kind === kind, `expected server-side ${kind}, got ${event?.kind ?? 'none'}`);
  assert(Number.isInteger(event.sequence) && event.sequence > 0, `${kind} has no server sequence`);
  assert(!Number.isNaN(Date.parse(event.occurredAtUtc)), `${kind} has no valid server UTC time`);
  for (const [property, value] of Object.entries(expected)) {
    assert(
      event[property] === value,
      `${kind}.${property} should equal ${JSON.stringify(value)}, got ${JSON.stringify(event[property])}`,
    );
  }
  return event;
}

/**
 * The store is global to one process, so run against a quiet local instance.
 * Any unrelated interaction during capture fails the exact-count invariant,
 * rather than silently including somebody else's behaviour as evidence.
 */
async function nextEvent(request, previous, kind, expected = {}) {
  let latest;
  for (let attempt = 0; attempt < 50; attempt += 1) {
    latest = await readHistory(request);
    if (latest.totalRecorded > previous.totalRecorded) break;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assert(
    latest.totalRecorded === previous.totalRecorded + 1,
    `${kind} must append exactly one interaction`,
  );
  const event = requireEvent(latest.events[0], kind, expected);
  assert(event.sequence > (previous.events[0]?.sequence ?? 0), `${kind} sequence must increase`);
  return { history: latest, event };
}

async function unchanged(request, expected, description) {
  const current = await readHistory(request);
  assert(
    current.totalRecorded === expected.totalRecorded,
    `${description} unexpectedly created a training event`,
  );
  return current;
}

async function screenshot(page, element, filePath) {
  await page.evaluate(() => document.fonts.ready);
  await element.screenshot({ path: filePath, animations: 'disabled', timeout: 60_000 });
}

async function waitReady(page) {
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible({ timeout: waitMs });
  await expect(page.getByRole('heading', { name: 'Find devices' })).toBeVisible();
  await expect(page.getByTestId('device-search-count')).toContainText('matching devices');
}

/**
 * Checks responsive layout from Chromium's actual DOM geometry. A horizontally
 * scrollable history TABLE is allowed; the whole page must not overflow.
 */
async function assertNoDocumentOverflow(page) {
  const geometry = await page.evaluate(() => ({
    viewport: window.innerWidth,
    documentWidth: document.documentElement.scrollWidth,
  }));
  assert(
    geometry.documentWidth <= geometry.viewport + 1,
    `responsive document overflows by ${geometry.documentWidth - geometry.viewport}px`,
  );
  return geometry;
}

/** Take the exact current server event as a compact audit artefact. */
function recordStep(collection, label, event) {
  collection.push({ label, ...event });
  console.log(`  VERIFIED ${label}: ${event.kind} #${event.sequence}`);
}

async function main() {
  const baseUrl = readOptions(process.argv.slice(2));
  const stageDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'smartx-p208-evidence-'));
  const request = await playwrightRequest.newContext({ baseURL: baseUrl, timeout: waitMs });
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({
    viewport: { width: 1480, height: 980 },
    deviceScaleFactor: 2,
    reducedMotion: 'reduce',
  });

  const token = randomBytes(5).toString('hex').toUpperCase();
  const sourceName = `P208 Evidence Source ${token}`;
  const actuatorName = `P208 Evidence Relay ${token}`;
  const actuatorMac = `02:${randomBytes(5).toString('hex').match(/.{2}/g).join(':')}`.toUpperCase();
  const screenshotNames = [
    '05-search-empty-results.png',
    '05-search-and-filter.png',
    '05-search-alert-association.png',
    '05-search-command-evidence.png',
    '05-search-mobile.png',
  ];
  const verifiedEvents = [];
  let report;

  try {
    console.log(`P2-08 evidence capture against ${baseUrl}`);
    const health = await jsonRequest(request, 'get', '/api/health');
    assert(health !== null, 'health endpoint returned null');

    // Verify the P2-08 endpoint exists BEFORE mutating demo data.
    await readHistory(request);
    await readSearch(request, { text: 'NONEXISTENT_EVIDENCE_QUERY' });

    console.log('1/8 Create two registered demo devices and one live disconnect episode');
    const source = await registerDevice(
      request,
      sourceName,
      'UniqueIdentifier',
      'Environmental',
      'Float',
      `p208-source-${token.toLowerCase()}`,
    );
    const target = await registerDevice(
      request,
      actuatorName,
      'MacAddress',
      'Actuator',
      'Boolean',
      actuatorMac,
    );
    await setDeviceStatus(request, source.id, 'Online');
    await setDeviceStatus(request, source.id, 'Offline');
    await setDeviceStatus(request, target.id, 'Online');

    const sourceCriteria = {
      text: sourceName,
      category: 'Environmental',
      status: 'Offline',
      hasActiveCondition: 'true',
      conditionType: 'SensorDisconnected',
    };
    const sourceServerResult = await readSearch(request, sourceCriteria);
    assertSingleResult(sourceServerResult, source.id, 'server combined filters');
    assert(
      sourceServerResult.devices[0].highestPriorityIssueId,
      'source device must expose an active issue episode',
    );
    const activeConditions = await jsonRequest(
      request,
      'get',
      '/api/realtime/snapshot/active-conditions',
    );
    assert(
      activeConditions.activeConditions.some(
        (item) => item.sensorProfileId === source.id && item.conditionType === 'SensorDisconnected',
      ),
      'source must have a real HashSet-backed SensorDisconnected condition',
    );

    const baseline = await readHistory(request);
    await page.goto(`${baseUrl}/realtime`, { waitUntil: 'domcontentloaded' });
    await waitReady(page);
    await expect(page.getByText('SignalR connected')).toBeVisible({ timeout: waitMs });
    await unchanged(request, baseline, 'opening the operations console');

    console.log('2/8 Show deliberate empty results without false results or auto interactions');
    const textInput = page.getByRole('searchbox', { name: 'Name / deployment text' });
    const statusInput = page.getByRole('combobox', { name: 'Operational status' });
    const categoryInput = page.getByRole('combobox', { name: 'Category' });
    const conditionInput = page.getByRole('combobox', { name: 'Active conditions' });
    const conditionTypeInput = page.getByRole('combobox', { name: 'Condition type' });

    await textInput.fill(sourceName);
    await categoryInput.selectOption('Environmental');
    await statusInput.selectOption('Online'); // Deliberately wrong for this offline source.
    await conditionInput.selectOption('true');
    await conditionTypeInput.selectOption('SensorDisconnected');
    await unchanged(request, baseline, 'typing and editing unsubmitted filters');

    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('0 matching devices');
    await expect(page.getByTestId('device-search-empty')).toBeVisible();
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('FiltersChanged');
    let cursor = await nextEvent(request, baseline, 'FiltersChanged', { resultCount: 0 });
    recordStep(verifiedEvents, 'Intentional unmatched filters', cursor.event);
    await screenshot(
      page,
      page.locator('.realtime-search'),
      path.join(stageDirectory, screenshotNames[0]),
    );

    console.log('3/8 Show combined filtering and a genuine submitted search');
    await statusInput.selectOption('Offline');
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    cursor = await nextEvent(request, cursor.history, 'FiltersChanged', {
      resultCount: 1,
      category: 'Environmental',
      status: 'Offline',
      conditionType: 'SensorDisconnected',
    });
    recordStep(verifiedEvents, 'Intentional matching filters', cursor.event);

    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('SearchSubmitted');
    await expect(page.getByTestId('device-search-result')).toHaveCount(1);
    await expect(page.getByTestId('device-search-result')).toContainText(sourceName);
    cursor = await nextEvent(request, cursor.history, 'SearchSubmitted', { resultCount: 1 });
    recordStep(verifiedEvents, 'Explicit combined-filter search', cursor.event);
    await screenshot(
      page,
      page.locator('.realtime-search'),
      path.join(stageDirectory, screenshotNames[1]),
    );

    console.log('4/8 Inspect device and view its real active issue episode');
    await page.getByRole('button', { name: 'Inspect device' }).click();
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(source.id);
    cursor = await nextEvent(request, cursor.history, 'DeviceViewed', { deviceId: source.id });
    recordStep(verifiedEvents, 'Explicit device view', cursor.event);

    await page.getByRole('button', { name: 'View active alert' }).click();
    await expect(page.getByText('Source alert selected')).toBeVisible();
    cursor = await nextEvent(request, cursor.history, 'AlertViewed', { deviceId: source.id });
    const sourceEpisodeId = cursor.event.alertEpisodeId;
    assert(
      typeof sourceEpisodeId === 'string' && sourceEpisodeId.length > 0,
      'AlertViewed must contain a genuine server-recorded episode ID',
    );

    /*
     * An earlier search returned its highest-priority issue at that instant.
     * Its priority order is NOT a durable identifier for the episode that an
     * operator chooses later: the dashboard can legitimately expose more
     * than one unresolved condition and recalculate their priority over time.
     *
     * Instead, independently read the current P2-06 HashSet projection and
     * verify that the recorded AlertViewed episode belongs to this exact
     * source device and is still unresolved. This preserves a meaningful
     * server-side invariant without relying on a stale priority ranking.
     */
    const currentConditions = await jsonRequest(
      request,
      'get',
      '/api/realtime/snapshot/active-conditions',
    );
    assert(
      Array.isArray(currentConditions.activeConditions),
      'the active-condition endpoint must return its server-owned condition list',
    );
    const sourceActiveConditions = currentConditions.activeConditions.filter(
      (condition) => condition.sensorProfileId === source.id,
    );
    const selectedActiveAlert = sourceActiveConditions.find(
      (condition) => condition.issueId === sourceEpisodeId,
    );
    assert(
      selectedActiveAlert !== undefined,
      `AlertViewed episode ${sourceEpisodeId} must still be active on source device ${source.id}; ` +
        `current episodes: ${sourceActiveConditions.map((condition) => condition.issueId).join(', ') || 'none'}`,
    );
    assert(
      sourceActiveConditions.some((condition) => condition.conditionType === 'SensorDisconnected'),
      'the offline demonstration source must retain its real SensorDisconnected condition',
    );
    recordStep(verifiedEvents, 'Explicit active alert view', cursor.event);

    console.log('5/8 Canonical MAC lookup and source-alert-to-actuator association');
    await textInput.fill('');
    await page
      .getByRole('textbox', { name: 'Exact physical identifier' })
      .fill(actuatorMac.toLowerCase().replaceAll(':', '-'));
    await categoryInput.selectOption('Actuator');
    await statusInput.selectOption('Online');
    await conditionInput.selectOption('');
    await conditionTypeInput.selectOption('');
    await unchanged(request, cursor.history, 'typing canonical MAC and other drafts');
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await expect(page.getByTestId('device-search-result')).toContainText(actuatorName);
    cursor = await nextEvent(request, cursor.history, 'SearchSubmitted', {
      resultCount: 1,
      canonicalIdentifier: actuatorMac,
      category: 'Actuator',
      status: 'Online',
    });
    recordStep(verifiedEvents, 'Canonical Dictionary identifier search', cursor.event);
    const targetServerResult = await readSearch(request, {
      deviceIdentifier: actuatorMac.toLowerCase().replaceAll(':', '-'),
      identifierKind: 'MacAddress',
      category: 'Actuator',
      status: 'Online',
    });
    assertSingleResult(targetServerResult, target.id, 'server canonical lookup');

    await page.getByRole('button', { name: 'Associate with source alert' }).click();
    await expect(page.getByTestId('search-recorded-receipt')).toContainText('source alert episode');
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);
    cursor = await nextEvent(request, cursor.history, 'SearchSubmitted', {
      deviceId: target.id,
      alertEpisodeId: sourceEpisodeId,
      canonicalIdentifier: actuatorMac,
      resultCount: 1,
    });
    assert(
      cursor.event.commandId === null,
      'search association must not pretend a command succeeded',
    );
    recordStep(verifiedEvents, 'Alert-to-actuator search association', cursor.event);
    await screenshot(
      page,
      page.locator('.realtime-search'),
      path.join(stageDirectory, screenshotNames[2]),
    );

    console.log('6/8 Prove real command events can only originate from command service');
    const relayOn = page.getByRole('button', { name: 'Relay On' });
    const undo = page.getByRole('button', { name: 'Undo latest command' });
    const commandState = page.getByTestId('realtime-commanded-state');
    await expect(relayOn).toBeEnabled();
    await expect(commandState).toHaveText('Off');
    await relayOn.click();
    await expect(commandState).toHaveText('On');
    cursor = await nextEvent(request, cursor.history, 'CommandExecuted', { deviceId: target.id });
    assert(cursor.event.commandId, 'server command event must contain a real command ID');
    recordStep(verifiedEvents, 'Successful relay command from server', cursor.event);
    await expect(undo).toBeEnabled();
    await undo.click();
    await expect(commandState).toHaveText('Off');
    cursor = await nextEvent(request, cursor.history, 'CommandUndone', { deviceId: target.id });
    assert(cursor.event.commandId, 'server Undo event must contain a real reversal ID');
    recordStep(verifiedEvents, 'Successful Stack Undo from server', cursor.event);

    const audit = await jsonRequest(request, 'get', `/api/realtime/devices/${target.id}/commands`);
    assert(
      audit.totalAuditCount === 2,
      'real actuator audit must contain one command and one Undo',
    );
    assert(audit.actuator.commandedState === 'Off', 'Undo must restore original relay state');
    assert(audit.actuator.undoDepth === 0, 'Undo must empty the single-entry Stack');
    assert(
      audit.recentAudit.some((item) => item.isReversal),
      'command audit must include linked reversal',
    );
    const commandPanel = page.locator('.realtime-operator-panels');
    await expect(commandPanel).toContainText('Undo reversal');
    await screenshot(page, commandPanel, path.join(stageDirectory, screenshotNames[3]));

    console.log('7/8 Verify passive refresh and pillar navigation create no new events');
    const snapshotResponse = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/realtime/snapshot' && response.status() === 200,
      { timeout: waitMs },
    );
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await snapshotResponse;
    await expect(page.getByTestId('device-search-count')).toHaveText('1 matching devices');
    await unchanged(request, cursor.history, 'explicit refresh (read-only)');

    await page.getByRole('link', { name: /Sensor Data Ingestion and Telemetry/ }).click();
    await page.getByRole('link', { name: /Real-Time Command Stream and History/ }).click();
    await waitReady(page);
    await expect(page.getByRole('textbox', { name: 'Exact physical identifier' })).toHaveValue(
      actuatorMac,
    );
    await expect(categoryInput).toHaveValue('Actuator');
    await expect(statusInput).toHaveValue('Online');
    await expect(page.getByRole('combobox', { name: 'Selected device' })).toHaveValue(target.id);
    await expect(page.getByText('Source alert selected')).toBeVisible();
    await unchanged(request, cursor.history, 'back/forward pillar navigation and GET refreshes');

    console.log('8/8 Verify responsive browser layout and final API provenance');
    await page.setViewportSize({ width: 390, height: 844 });
    const geometry = await assertNoDocumentOverflow(page);
    await screenshot(
      page,
      page.locator('.realtime-search'),
      path.join(stageDirectory, screenshotNames[4]),
    );
    await unchanged(request, cursor.history, 'responsive viewport change');

    // A rejected identifier is not a completed search or training signal.
    await page.getByRole('textbox', { name: 'Exact physical identifier' }).fill('invalid-mac');
    await page.getByRole('button', { name: 'Search devices' }).click();
    await expect(page.locator('.realtime-search').getByRole('alert')).toContainText(
      'Search rejected or unavailable',
    );
    await unchanged(request, cursor.history, 'server-rejected malformed MAC search');

    const events = await readHistory(request);
    const expectedNewEvents = 9; // 2 filter changes, 3 searches, 2 views, 1 command and 1 Undo.
    assert(
      events.totalRecorded - baseline.totalRecorded === expectedNewEvents,
      `expected exactly ${expectedNewEvents} deliberate interactions`,
    );
    assert(
      verifiedEvents.length === expectedNewEvents,
      'all expected events must be verified individually',
    );
    assert(
      verifiedEvents.every(
        (item, index) => index === 0 || item.sequence > verifiedEvents[index - 1].sequence,
      ),
      'server assigned sequences must be strictly increasing',
    );

    report = {
      issue: 'P2-08',
      type: 'real-ui-plus-server-verified-interaction-evidence',
      capturedAtUtc: new Date().toISOString(),
      applicationOrigin: baseUrl,
      sessionScope: 'single process-local Smart-X API instance',
      note: 'Not authenticated-user behaviour, durable storage, or a prediction engine.',
      demonstrationDevices: {
        disconnectedSource: {
          id: source.id,
          name: sourceName,
          category: source.category,
          status: 'Offline',
        },
        actuator: {
          id: target.id,
          name: actuatorName,
          canonicalMac: actuatorMac,
          category: target.category,
          status: 'Online',
        },
      },
      canonicalLookup: {
        submittedEquivalent: actuatorMac.toLowerCase().replaceAll(':', '-'),
        canonicalServerValue: targetServerResult.appliedFilters.deviceIdentifier,
        totalMatches: targetServerResult.totalMatches,
        matchedDeviceId: targetServerResult.devices[0].device.id,
      },
      combinedFilter: {
        query: sourceCriteria,
        serverMatchCount: sourceServerResult.totalMatches,
        alertEpisodeId: sourceEpisodeId,
        selectedAlertConditionType: selectedActiveAlert.conditionType,
        selectedAlertVerifiedInActiveConditions: true,
        noMatchQueryResult: 0,
      },
      interactionStore: {
        capacity: events.capacity,
        baselineTotalRecorded: baseline.totalRecorded,
        finalTotalRecorded: events.totalRecorded,
        verifiedNewEvents: verifiedEvents.length,
        passiveRefreshAddedEvents: 0,
        navigationAddedEvents: 0,
        rejectedIdentifierAddedEvents: 0,
        events: verifiedEvents,
      },
      commandStore: {
        totalAcceptedAudit: audit.totalAuditCount,
        finalCommandedState: audit.actuator.commandedState,
        finalUndoDepth: audit.actuator.undoDepth,
        linkedReversalPresent: true,
      },
      responsiveViewport: geometry,
      screenshots: screenshotNames,
    };

    await fs.mkdir(evidenceDirectory, { recursive: true });
    // Publish only validated, browser-captured evidence. This does not capture
    // the complete history of other sensors or unrelated operator sessions.
    for (const file of screenshotNames) {
      await fs.copyFile(path.join(stageDirectory, file), path.join(evidenceDirectory, file));
    }
    await fs.writeFile(
      path.join(evidenceDirectory, '05-search-and-filter-summary.json'),
      `${JSON.stringify(report, null, 2)}\n`,
      { encoding: 'utf8', mode: 0o644 },
    );
    console.log(
      '\nSUCCESS — all nine server-recorded interactions and passive non-events verified.',
    );
    console.log(`Screenshots and JSON saved in: ${evidenceDirectory}`);
  } finally {
    await page.close();
    await browser.close();
    await request.dispose();
    await fs.rm(stageDirectory, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error('\nP2-08 evidence capture did not pass validation:');
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
});
