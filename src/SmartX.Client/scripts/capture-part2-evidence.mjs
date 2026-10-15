/**
 * PROG7312 / Smart-X P2-07 — reproducible React evidence capture.
 *
 * This script drives the REAL React operations console against a running
 * Smart-X HTTP deployment. It never fabricates device state or screenshots.
 * The browser invokes existing backend APIs through the application's normal
 * simulator and command controls, then independent HTTP reads verify results.
 *
 * Run from src/SmartX.Client:
 *   node scripts/capture-part2-evidence.mjs
 *   node scripts/capture-part2-evidence.mjs --base-url http://localhost:9090
 *   node scripts/capture-part2-evidence.mjs --load-only --devices 100 --readings 100
 *
 * Screenshots are only promoted to docs/evidence/part2 after all checks pass.
 * No encryption keys, tokens, or runtime files are included in the evidence.
 */

import { chromium, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '../../..');
const evidenceDirectory = path.join(repositoryRoot, 'docs/evidence/part2');
const scenarioTimeoutMs = 90_000;
const loadTimeoutMs = 600_000;

/** Parse only explicit, supported flags; reject surprising inputs. */
function parseOptions(argumentsList) {
  const settings = {
    baseUrl: 'http://localhost:8080',
    loadOnly: false,
    devices: 100,
    readings: 100,
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--help' || argument === '-h') {
      console.log(`Usage:
  node scripts/capture-part2-evidence.mjs [--base-url http://localhost:8080]
  node scripts/capture-part2-evidence.mjs --load-only [--devices 100] [--readings 100]

Normal mode: real desktop, active-condition, and mobile React screenshots.
Load-only mode: real finite-load screenshot and submission/processing summary.
The target must be localhost or 127.0.0.1 to avoid modifying a live service.`);
      process.exit(0);
    }

    if (argument === '--load-only') {
      settings.loadOnly = true;
      continue;
    }

    if (['--base-url', '--devices', '--readings'].includes(argument)) {
      if (index + 1 >= argumentsList.length) {
        throw new Error(`Missing value for ${argument}.`);
      }
      const rawValue = argumentsList[++index];
      if (argument === '--base-url') {
        settings.baseUrl = rawValue.replace(/\/+$/, '');
      } else {
        // A number input must be a decimal integer, not a fractional value.
        if (!/^[0-9]+$/.test(rawValue)) {
          throw new Error(`${argument} must be a positive whole number.`);
        }
        settings[argument === '--devices' ? 'devices' : 'readings'] = Number(rawValue);
      }
      continue;
    }

    throw new Error(`Unknown option: ${argument}. Use --help for supported arguments.`);
  }

  const parsedUrl = new URL(settings.baseUrl);
  if (
    !['http:', 'https:'].includes(parsedUrl.protocol) ||
    !['localhost', '127.0.0.1'].includes(parsedUrl.hostname) ||
    parsedUrl.username ||
    parsedUrl.password ||
    parsedUrl.pathname !== '/' ||
    parsedUrl.search ||
    parsedUrl.hash
  ) {
    throw new Error('Evidence capture only accepts a local HTTP(S) origin without credentials.');
  }

  if (
    !Number.isInteger(settings.devices) ||
    !Number.isInteger(settings.readings) ||
    settings.devices < 1 ||
    settings.devices > 100 ||
    settings.readings < 1 ||
    settings.readings > 100 ||
    settings.devices * settings.readings > 10_000
  ) {
    throw new Error('Finite load must be 1–100 devices and 1–100 readings each (10,000 maximum).');
  }

  return settings;
}

/** All verification reads use the real, same-origin ASP.NET Core API. */
async function getJson(request, baseUrl, route) {
  const response = await request.get(`${baseUrl}${route}`, { timeout: 20_000 });
  if (!response.ok()) {
    throw new Error(
      `API ${route} returned HTTP ${response.status()}: ${(await response.text()).slice(0, 250)}`,
    );
  }
  return response.json();
}

/** Wait for asynchronous worker state without assuming a processing latency. */
async function eventually(description, read, predicate, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  let lastResult;
  while (Date.now() < deadline) {
    lastResult = await read();
    if (predicate(lastResult)) return lastResult;
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  throw new Error(
    `Timed out verifying ${description}. Latest result: ${JSON.stringify(lastResult).slice(0, 500)}`,
  );
}

function requireInvariant(condition, message) {
  if (!condition) throw new Error(`Evidence invariant failed: ${message}`);
}

/** Keep very large counters readable regardless of locale space separators. */
function displayedInteger(text) {
  return Number(text.replace(/[^0-9]/g, ''));
}

async function confirmConsoleReady(page) {
  await page.goto('/realtime', { waitUntil: 'domcontentloaded' });
  await expect(page.getByText('HTTP snapshot ready')).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole('heading', { name: 'IoT traffic simulator' })).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
}

/** Drive the actual React simulation button, then wait for its real verification. */
async function runScenario(page, label, selectedName, uniqueResultFragment) {
  await page.getByRole('button', { name: label, exact: true }).click();
  const select = page.getByRole('combobox', { name: 'Selected device' });
  await expect(select.locator('option:checked')).toContainText(selectedName, {
    timeout: scenarioTimeoutMs,
  });
  await expect(page.locator('.realtime-simulator-result')).toContainText(uniqueResultFragment, {
    timeout: scenarioTimeoutMs,
  });
  await expect(
    page.getByText('Scenario completed. All displayed confirmations came from the real API.'),
  ).toBeVisible();
  return select.inputValue();
}

/** Screenshots remain pixel-faithful to Chromium's rendered React document. */
async function takeScreenshot(page, destination) {
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({
    path: destination,
    fullPage: true,
    animations: 'disabled',
    timeout: 60_000,
  });
}

async function ensureMobileNoOverflow(page) {
  const sizes = await page.evaluate(() => ({
    viewport: window.innerWidth,
    document: document.documentElement.scrollWidth,
  }));
  requireInvariant(
    sizes.document <= sizes.viewport + 1,
    `mobile document horizontally overflows (${sizes.document}px > ${sizes.viewport}px)`,
  );
  return sizes;
}

/** Standard evidence: complete command lifecycle and unique conditions. */
async function captureWorkspace({ page, request, baseUrl, stagingDirectory }) {
  await confirmConsoleReady(page);

  const initialProcessing = await getJson(request, baseUrl, '/api/realtime/processing');
  requireInvariant(!initialProcessing.ordinaryDispatchPaused, 'ordinary dispatch must be running');
  requireInvariant(
    initialProcessing.workerStatus !== 'Faulted' && !initialProcessing.failure,
    'telemetry worker must be healthy',
  );

  console.log('1/6 Running genuine Boolean actuator scenario via React...');
  const actuatorId = await runScenario(
    page,
    'Run boolean actuator',
    'P2-07 actuator',
    'RelayOn, accepted no-op and Undo produced three audited operations',
  );

  const initialAudit = await getJson(
    request,
    baseUrl,
    `/api/realtime/devices/${encodeURIComponent(actuatorId)}/commands`,
  );
  requireInvariant(
    initialAudit.totalAuditCount === 3,
    'scenario must record three audited commands',
  );
  requireInvariant(
    initialAudit.actuator.commandedState === 'Off' && initialAudit.actuator.undoDepth === 0,
    'scenario Undo must restore Off and empty the Stack',
  );

  console.log('2/6 Executing one additional relay command using the real UI...');
  const commandState = page.getByTestId('realtime-commanded-state');
  const undoDepth = page.getByTestId('realtime-undo-depth');
  await expect(page.getByRole('button', { name: 'Relay On' })).toBeEnabled();
  await page.getByRole('button', { name: 'Relay On' }).click();
  await expect(commandState).toHaveText('On', { timeout: 20_000 });
  await expect(undoDepth).toHaveText('1');
  await expect(page.getByRole('button', { name: 'Undo latest command' })).toBeEnabled();
  await expect(page.getByRole('table', { name: 'Chronological device history' })).toContainText(
    'Undo reversal',
  );

  const actuatorAudit = await eventually(
    'committed RelayOn and server Stack depth',
    () => getJson(request, baseUrl, `/api/realtime/devices/${actuatorId}/commands`),
    (value) =>
      value.totalAuditCount === 4 &&
      value.actuator.commandedState === 'On' &&
      value.actuator.undoDepth === 1,
  );

  const actuatorHistory = await eventually(
    'completed Boolean telemetry and linked Undo history',
    () => getJson(request, baseUrl, `/api/realtime/devices/${actuatorId}/history?limit=30`),
    (value) =>
      value.events.filter((entry) => entry.kind === 'TelemetryProcessed').length === 2 &&
      value.events.filter((entry) => entry.kind === 'RelayUndo').length === 1 &&
      value.events.filter((entry) => entry.kind === 'RelayCommand').length === 3,
  );

  console.log('3/6 Capturing real desktop command/history interface...');
  await takeScreenshot(page, path.join(stagingDirectory, '02-live-command-stream.png'));

  console.log('4/6 Running verified telemetry escalation/recovery/recurrence...');
  const spikeId = await runScenario(
    page,
    'Run numeric spike',
    'P2-07 spike',
    'A later spike reused the stable key with a new issue ID',
  );

  await expect(
    page.locator('.realtime-condition').filter({ hasText: 'TelemetrySpike' }),
  ).toContainText('Critical', { timeout: 20_000 });
  const spikeConditions = await getJson(
    request,
    baseUrl,
    '/api/realtime/snapshot/active-conditions',
  );
  const episode = spikeConditions.activeConditions.find(
    (condition) =>
      condition.sensorProfileId === spikeId && condition.conditionType === 'TelemetrySpike',
  );
  requireInvariant(episode !== undefined, 'spike must remain active after recurrence');
  requireInvariant(
    episode.severity === 'Critical' && episode.occurrenceCount === 1,
    'recurrent spike must be Critical with a fresh occurrence count of one',
  );
  const spikeHistory = await eventually(
    'all seven spike readings processed',
    () => getJson(request, baseUrl, `/api/realtime/devices/${spikeId}/history?limit=30`),
    (value) => value.events.filter((entry) => entry.kind === 'TelemetryProcessed').length === 7,
  );
  await takeScreenshot(page, path.join(stagingDirectory, '02-active-conditions.png'));

  console.log('5/6 Testing the genuine 390px mobile layout...');
  await page.getByRole('combobox', { name: 'Selected device' }).selectOption(actuatorId);
  await expect(page.getByTestId('realtime-commanded-state')).toHaveText('On');
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('heading', { name: 'Operations console' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Actuator commands' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Typed load demonstration' })).toBeVisible();
  const mobileSizes = await ensureMobileNoOverflow(page);
  await takeScreenshot(page, path.join(stagingDirectory, '09-responsive-part2.png'));

  // This fourth image is a readable, unmodified Chromium element screenshot
  // of the actual simulator instead of a miniature thumbnail of a tall page.
  await page.locator('.realtime-simulator').screenshot({
    path: path.join(stagingDirectory, '09-responsive-part2-simulator.png'),
    animations: 'disabled',
    timeout: 30_000,
  });

  const endProcessing = await getJson(request, baseUrl, '/api/realtime/processing');
  console.log('6/6 HTTP verification complete. All screenshot claims are grounded.');

  return {
    captureType: 'Real Smart-X React browser and HTTP API evidence',
    capturedAtUtc: new Date().toISOString(),
    baseUrl,
    actuator: {
      sensorId: actuatorId,
      commandedState: actuatorAudit.actuator.commandedState,
      undoDepth: actuatorAudit.actuator.undoDepth,
      acceptedCommandAuditCount: actuatorAudit.totalAuditCount,
      chronologicalEventCount: actuatorHistory.totalRecordedEvents,
      processedBooleanReadings: actuatorHistory.events.filter(
        (entry) => entry.kind === 'TelemetryProcessed',
      ).length,
      reversalCount: actuatorHistory.events.filter((entry) => entry.kind === 'RelayUndo').length,
    },
    recurrentSpike: {
      sensorId: spikeId,
      membershipKey: episode.membershipKey,
      issueId: episode.issueId,
      severity: episode.severity,
      occurrenceCount: episode.occurrenceCount,
      processedFloatReadings: spikeHistory.events.filter(
        (entry) => entry.kind === 'TelemetryProcessed',
      ).length,
    },
    processing: {
      initiallyAccepted: initialProcessing.acceptedCount,
      acceptedAtCapture: endProcessing.acceptedCount,
      processedAtCapture: endProcessing.processedCount,
      pendingAtCapture: endProcessing.pendingCount,
      ordinaryPendingAtCapture: endProcessing.ordinaryPendingCount,
      criticalPendingAtCapture: endProcessing.criticalPendingCount,
      ordinaryDispatchPaused: endProcessing.ordinaryDispatchPaused,
    },
    mobile: mobileSizes,
    screenshots: [
      '02-live-command-stream.png',
      '02-active-conditions.png',
      '09-responsive-part2.png',
      '09-responsive-part2-simulator.png',
    ],
  };
}

/** Optional larger rubric demonstration; records confirmations separately. */
async function captureFiniteLoad({ page, request, baseUrl, stagingDirectory, devices, readings }) {
  await confirmConsoleReady(page);

  const before = await getJson(request, baseUrl, '/api/realtime/processing');
  requireInvariant(!before.ordinaryDispatchPaused, 'ordinary dispatch must be running before load');
  requireInvariant(!before.failure && before.workerStatus !== 'Faulted', 'worker must be healthy');
  requireInvariant(
    before.pendingCount === 0 && before.outstandingDispatchedCount === 0,
    'wait for the existing worker queues to drain before taking load evidence',
  );

  await page.getByRole('spinbutton', { name: 'Load devices' }).fill(String(devices));
  await page.getByRole('spinbutton', { name: 'Readings per device' }).fill(String(readings));
  await expect(page.getByRole('button', { name: 'Run finite load' })).toBeEnabled();
  const target = devices * readings;
  console.log(
    `Running finite, API-driven load: ${devices} devices x ${readings} = ${target} readings.`,
  );

  await page.getByRole('button', { name: 'Run finite load' }).click();
  await expect(page.locator('.realtime-simulator-result')).toContainText(
    `${target} typed readings confirmed by ${devices} actual batch API responses.`,
    { timeout: loadTimeoutMs },
  );
  await expect(
    page.getByText('Scenario completed. All displayed confirmations came from the real API.'),
  ).toBeVisible();

  const confirmedDevices = displayedInteger(
    await page.getByTestId('simulator-devices').innerText(),
  );
  const confirmedReadings = displayedInteger(
    await page.getByTestId('simulator-accepted').innerText(),
  );
  requireInvariant(confirmedDevices === devices, 'actual HTTP-confirmed device count must match');
  requireInvariant(confirmedReadings === target, 'actual HTTP-confirmed reading count must match');

  const after = await getJson(request, baseUrl, '/api/realtime/processing');
  requireInvariant(
    after.acceptedCount - before.acceptedCount >= target,
    'server acceptance delta must include the accepted load',
  );

  await takeScreenshot(page, path.join(stagingDirectory, '02-finite-load.png'));
  return {
    captureType: 'Real React finite-load demonstration (acceptance, not a latency benchmark)',
    capturedAtUtc: new Date().toISOString(),
    baseUrl,
    requested: { devices, readingsPerDevice: readings, totalReadings: target },
    httpConfirmed: { registeredDevices: confirmedDevices, acceptedReadings: confirmedReadings },
    processWideBefore: {
      accepted: before.acceptedCount,
      processed: before.processedCount,
      pending: before.pendingCount,
    },
    processWideAtCapture: {
      accepted: after.acceptedCount,
      processed: after.processedCount,
      pending: after.pendingCount,
      ordinaryPending: after.ordinaryPendingCount,
      criticalPending: after.criticalPendingCount,
    },
    processWideDelta: {
      accepted: after.acceptedCount - before.acceptedCount,
      processed: after.processedCount - before.processedCount,
    },
    interpretation:
      'HTTP acceptance is confirmed. Worker processing may continue after the screenshot. ' +
      'Process-wide counters can include other clients; no latency SLA is claimed.',
    screenshots: ['02-finite-load.png'],
  };
}

/** Stage screenshots in the OS temp directory; do not publish failed captures. */
async function main() {
  const options = parseOptions(process.argv.slice(2));
  const stagingDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'smartx-p207-evidence-'));
  let browser;

  try {
    // Fail early if the actual Docker / local React deployment is unavailable.
    browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({
      baseURL: options.baseUrl,
      viewport: { width: 1440, height: 1000 },
      deviceScaleFactor: 1,
      reducedMotion: 'reduce',
    });
    const page = await context.newPage();
    const health = await getJson(context.request, options.baseUrl, '/api/health');
    requireInvariant(health !== undefined, 'API health endpoint must be available');

    let result;
    if (options.loadOnly) {
      result = await captureFiniteLoad({
        page,
        request: context.request,
        baseUrl: options.baseUrl,
        stagingDirectory,
        devices: options.devices,
        readings: options.readings,
      });
    } else {
      result = await captureWorkspace({
        page,
        request: context.request,
        baseUrl: options.baseUrl,
        stagingDirectory,
      });
    }

    const summaryName = options.loadOnly
      ? '02-finite-load-summary.json'
      : '02-live-command-stream-summary.json';
    await fs.writeFile(
      path.join(stagingDirectory, summaryName),
      `${JSON.stringify(result, null, 2)}\n`,
      'utf8',
    );

    await fs.mkdir(evidenceDirectory, { recursive: true });
    for (const name of [...result.screenshots, summaryName]) {
      // Copy is used rather than moving across potentially different devices.
      await fs.copyFile(path.join(stagingDirectory, name), path.join(evidenceDirectory, name));
      console.log(`Saved: docs/evidence/part2/${name}`);
    }
    console.log('PASS: Smart-X real browser evidence was captured and verified.');
    await context.close();
  } finally {
    await browser?.close();
    await fs.rm(stagingDirectory, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(`P2-07 evidence capture failed: ${error.message}`);
  process.exitCode = 1;
});
