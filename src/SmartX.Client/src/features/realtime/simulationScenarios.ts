import { request } from '../../api/apiClient';
import {
  executeRelayCommand,
  getActuatorCommandHistory,
  undoRelayCommand,
} from '../../api/commandsApi';
import {
  getRealtimeActiveConditions,
  getRealtimeHistory,
  getRealtimeProcessing,
  setOrdinaryDispatchPaused,
  type TelemetryProcessingResponse,
} from '../../api/realtimeApi';
import {
  changeSensorStatus,
  registerSensor,
  type RegisterSensorRequest,
  type SensorResponse,
  type TelemetryDataType,
} from '../../api/sensorsApi';
import {
  ingestTelemetry,
  ingestTelemetryBatch,
  type IngestTelemetryRequest,
} from '../../api/telemetryApi';

/**
 * This is an API exercise harness, not a local substitute for a sensor registry,
 * anomaly engine, queue or command stack. Every mutation uses the existing
 * Part 1/Part 2 HTTP endpoints. Only completed HTTP receipts are counted.
 */
export type SimulationScenario =
  'normal' | 'spike' | 'actuator' | 'disconnect' | 'priority' | 'load';

export interface SimulationSettings {
  readonly deviceCount: number;
  readonly readingsPerDevice: number;
}

export interface SimulationProgress {
  readonly phase: string;
  readonly registeredDevices: number;
  readonly acceptedReadings: number;
  readonly acceptedBatches: number;
  readonly acceptedCommands: number;
  readonly targetDevices: number;
  readonly targetReadings: number;
  readonly firstDeviceId: string | null;
}

export interface SimulationResult extends SimulationProgress {
  readonly scenario: SimulationScenario;
  readonly observedProcessAcceptedDelta: number;
  readonly observedProcessProcessedDelta: number;
  readonly pendingAtEnd: number;
  readonly elapsedMilliseconds: number;
  readonly verifiedObservations: readonly string[];
}

export interface SimulationOptions {
  readonly scenario: SimulationScenario;
  readonly settings: SimulationSettings;
  readonly signal: AbortSignal;
  readonly onProgress: (progress: SimulationProgress) => void;
}

/**
 * Bounded demonstration settings. The default 100 x 100 makes exactly 10,000
 * typed packets but the browser never allocates all 10,000 at once.
 */
export const maximumLoadDevices = 100;
export const maximumReadingsPerDevice = 100;
export const maximumLoadReadings = 10_000;
export const defaultLoadSettings: SimulationSettings = {
  deviceCount: 100,
  readingsPerDevice: 100,
};

const batchSize = 25;
const pollingIntervalMs = 100;
const verificationTimeoutMs = 15_000;

/** Type-safe payload construction preserves true/false and numeric zero. */
function createReading(
  type: TelemetryDataType,
  value: number | boolean,
  capturedAtUtc: string,
): IngestTelemetryRequest {
  return {
    capturedAtUtc,
    floatValue: type === 'Float' ? (value as number) : null,
    integerValue: type === 'Integer' ? (value as number) : null,
    booleanValue: type === 'Boolean' ? (value as boolean) : null,
  };
}

/** Generate real historical capture times; none are in the future. */
function capturedAt(baseTime: number, offsetSeconds: number): string {
  return new Date(baseTime + offsetSeconds * 1_000).toISOString();
}

/**
 * Registers a clearly labelled demonstration device. The generated identifier
 * is under the Domain's 64-character limit and is unique across repeated runs.
 */
async function createDevice(
  scenario: string,
  type: TelemetryDataType,
  signal: AbortSignal,
): Promise<SensorResponse> {
  const unique = crypto.randomUUID();
  const requestBody: RegisterSensorRequest = {
    name: `P2-07 ${scenario} ${unique.slice(0, 8)}`,
    deviceIdentifier: `p207-${scenario.toLowerCase()}-${unique}`,
    identifierKind: 'UniqueIdentifier',
    deploymentLocation: {
      facility: 'Smart-X Demonstration Lab',
      zone: 'Part 2 Simulator',
      nodeId: `node-${unique.slice(0, 8)}`,
    },
    category:
      type === 'Boolean' ? 'Actuator' : type === 'Integer' ? 'PowerConsumption' : 'Environmental',
    telemetryDataType: type,
  };

  return registerSensor(requestBody, signal);
}

/**
 * Attach genuine encrypted configuration evidence, avoiding an unrelated
 * missing-configuration warning for every generated demonstration device.
 * The server validates and encrypts the multipart upload as in Part 1.
 */
async function attachDemoConfiguration(sensorId: string, signal: AbortSignal): Promise<void> {
  const form = new FormData();
  form.append(
    'file',
    new Blob(['{"enabled":true,"sampleRateSeconds":5}'], { type: 'application/json' }),
    'p207-simulation-config.json',
  );

  await request(`/api/sensors/${encodeURIComponent(sensorId)}/attachments?category=Configuration`, {
    method: 'POST',
    body: form,
    signal,
  });
}

/** Abortable polling without artificial worker delays or browser-side state. */
async function waitFor<T>(
  description: string,
  signal: AbortSignal,
  inspect: () => Promise<T | null>,
): Promise<T> {
  const deadline = Date.now() + verificationTimeoutMs;
  while (Date.now() < deadline) {
    signal.throwIfAborted();
    const result = await inspect();
    if (result !== null) return result;

    await new Promise<void>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, pollingIntervalMs);
      const onAbort = () => {
        window.clearTimeout(timer);
        reject(signal.reason ?? new DOMException('Simulation stopped.', 'AbortError'));
      };
      signal.addEventListener('abort', onAbort, { once: true });
      if (signal.aborted) onAbort();
    });
  }
  throw new Error(`Timed out waiting for ${description}. Check the actual worker diagnostics.`);
}

/** Wait for genuine successful worker-history records, not request acceptance. */
async function waitForProcessedHistory(
  sensorId: string,
  expectedCount: number,
  signal: AbortSignal,
): Promise<void> {
  await waitFor(`the ${expectedCount} processed history events`, signal, async () => {
    const result = await getRealtimeHistory(sensorId, Math.max(10, expectedCount), 'asc', signal);
    return result.events.filter((entry) => entry.kind === 'TelemetryProcessed').length >=
      expectedCount
      ? true
      : null;
  });
}

function assertLoadSettings(settings: SimulationSettings): void {
  if (
    !Number.isInteger(settings.deviceCount) ||
    settings.deviceCount < 1 ||
    settings.deviceCount > maximumLoadDevices ||
    !Number.isInteger(settings.readingsPerDevice) ||
    settings.readingsPerDevice < 1 ||
    settings.readingsPerDevice > maximumReadingsPerDevice ||
    settings.deviceCount * settings.readingsPerDevice > maximumLoadReadings
  ) {
    throw new RangeError(
      'Load is limited to 1-100 devices and 1-100 readings per device (10,000 maximum).',
    );
  }
}

/**
 * Runs one finite scenario. Registration and ingestion are real, and the
 * runner never writes to any server collection or manufactures UI snapshots.
 *
 * On Stop, accepted requests are NOT rolled back: they may still be processed
 * by the background worker. The priority test always attempts to restore the
 * global ordinary lane using a fresh non-cancelled HTTP request.
 */
export async function runSimulation({
  scenario,
  settings,
  signal,
  onProgress,
}: SimulationOptions): Promise<SimulationResult> {
  if (scenario === 'load') assertLoadSettings(settings);

  const startedAt = Date.now();
  const before = await getRealtimeProcessing(signal);
  if (before.workerStatus === 'Faulted' || before.workerStatus === 'Stopped' || before.failure) {
    throw new Error(
      'The telemetry worker is not healthy. Check server diagnostics before simulating.',
    );
  }
  if (before.ordinaryDispatchPaused) {
    throw new Error('Ordinary dispatch is paused. Resume it before starting a scenario.');
  }

  const counts = {
    registeredDevices: 0,
    acceptedReadings: 0,
    acceptedBatches: 0,
    acceptedCommands: 0,
    targetDevices: scenario === 'load' ? settings.deviceCount : 1,
    targetReadings:
      scenario === 'load'
        ? settings.deviceCount * settings.readingsPerDevice
        : scenario === 'spike'
          ? 7
          : scenario === 'normal' || scenario === 'priority'
            ? 3
            : scenario === 'actuator'
              ? 2
              : 0,
    firstDeviceId: null as string | null,
  };
  const verifiedObservations: string[] = [];

  function publish(phase: string): void {
    onProgress({ ...counts, phase });
  }

  async function prepare(scenarioLabel: string, type: TelemetryDataType): Promise<SensorResponse> {
    signal.throwIfAborted();
    const sensor = await createDevice(scenarioLabel, type, signal);
    counts.registeredDevices += 1;
    counts.firstDeviceId ??= sensor.id;
    publish(`Registered ${sensor.name}; uploading encrypted configuration`);
    await attachDemoConfiguration(sensor.id, signal);
    publish(`Configured ${sensor.name} using the existing attachment API`);
    return sensor;
  }

  async function send(
    sensor: SensorResponse,
    value: number | boolean,
    time: string,
  ): Promise<void> {
    await ingestTelemetry(sensor.id, createReading(sensor.telemetryDataType, value, time), signal);
    counts.acceptedReadings += 1;
    publish(`Server accepted ${counts.acceptedReadings} typed reading(s)`);
  }

  publish('Validating worker and preparing actual API requests');

  switch (scenario) {
    case 'normal': {
      const sensor = await prepare('normal', 'Float');
      await changeSensorStatus(sensor.id, 'Online', signal);
      const base = Date.now() - 90_000;
      await send(sensor, 20, capturedAt(base, 0));
      await send(sensor, 20.1, capturedAt(base, 10));
      await send(sensor, 20.2, capturedAt(base, 20));
      publish('Waiting for three successful worker completions');
      await waitForProcessedHistory(sensor.id, 3, signal);
      verifiedObservations.push(
        'Three Float packets were accepted and recorded after actual worker processing.',
      );
      break;
    }

    case 'spike': {
      const sensor = await prepare('spike', 'Float');
      await changeSensorStatus(sensor.id, 'Online', signal);
      const base = Date.now() - 120_000;
      const values = [100, 140, 300, 700, 701, 702, 1500];

      for (let index = 0; index < 4; index += 1) {
        await send(sensor, values[index], capturedAt(base, index * 10));
      }

      const initial = await getRealtimeActiveConditions(signal);
      const spike = initial.activeConditions.find(
        (condition) =>
          condition.sensorProfileId === sensor.id && condition.conditionType === 'TelemetrySpike',
      );
      if (!spike || spike.severity !== 'Critical' || spike.occurrenceCount !== 3) {
        throw new Error('Expected one escalated critical spike episode with three occurrences.');
      }
      const repeated = initial.activeConditions.find(
        (condition) =>
          condition.sensorProfileId === sensor.id && condition.conditionType === 'RepeatedFailure',
      );
      if (!repeated)
        throw new Error('Expected the separate repeated-failure condition after three spikes.');
      verifiedObservations.push(
        'One spike issue escalated Warning to Critical across three abnormal readings.',
      );
      verifiedObservations.push('A separate RepeatedFailure membership key was activated.');

      await send(sensor, values[4], capturedAt(base, 40));
      await send(sensor, values[5], capturedAt(base, 50));
      const recovered = await getRealtimeActiveConditions(signal);
      if (
        recovered.activeConditions.some(
          (condition) =>
            condition.sensorProfileId === sensor.id &&
            (condition.conditionType === 'TelemetrySpike' ||
              condition.conditionType === 'RepeatedFailure'),
        )
      ) {
        throw new Error(
          'The two stable readings did not clear the spike and repeated-failure keys.',
        );
      }
      verifiedObservations.push('Two consecutive stable readings removed both active memberships.');

      await send(sensor, values[6], capturedAt(base, 60));
      const recurrence = await getRealtimeActiveConditions(signal);
      const next = recurrence.activeConditions.find(
        (condition) =>
          condition.sensorProfileId === sensor.id && condition.conditionType === 'TelemetrySpike',
      );
      if (!next || next.issueId === spike.issueId || next.membershipKey !== spike.membershipKey) {
        throw new Error('Recurrence did not create a new issue episode under the stable key.');
      }
      await waitForProcessedHistory(sensor.id, 7, signal);
      verifiedObservations.push(
        'A later spike reused the stable key with a new issue ID; all seven packets processed.',
      );
      break;
    }

    case 'actuator': {
      const sensor = await prepare('actuator', 'Boolean');
      await changeSensorStatus(sensor.id, 'Online', signal);
      const base = Date.now() - 90_000;
      await send(sensor, false, capturedAt(base, 0));
      await send(sensor, true, capturedAt(base, 10));
      publish('Executing real RelayOn, accepted no-op, and Undo');

      const on = await executeRelayCommand(sensor.id, 'RelayOn', signal);
      counts.acceptedCommands += 1;
      if (!on.command.stateChanged) throw new Error('Expected RelayOn to change Off to On.');
      const noOp = await executeRelayCommand(sensor.id, 'RelayOn', signal);
      counts.acceptedCommands += 1;
      if (noOp.command.stateChanged || noOp.actuator.undoDepth !== 1) {
        throw new Error('Repeated RelayOn incorrectly changed state or pushed a new Undo entry.');
      }
      const undo = await undoRelayCommand(sensor.id, on.command.commandId, signal);
      counts.acceptedCommands += 1;
      if (undo.actuator.commandedState !== 'Off' || undo.actuator.undoDepth !== 0) {
        throw new Error('Undo did not restore the real commanded Off state and empty the Stack.');
      }
      const audit = await getActuatorCommandHistory(sensor.id, signal);
      if (audit.totalAuditCount !== 3)
        throw new Error('Expected exactly three accepted audit entries.');
      await waitForProcessedHistory(sensor.id, 2, signal);
      publish('Verified two Boolean readings, accepted no-op and genuine Stack Undo');
      verifiedObservations.push(
        'Two reported Boolean readings were processed independently of commanded relay state.',
      );
      verifiedObservations.push(
        'RelayOn, accepted no-op and Undo produced three audited operations; final state Off, depth zero.',
      );
      break;
    }

    case 'disconnect': {
      const sensor = await prepare('disconnect', 'Float');
      await changeSensorStatus(sensor.id, 'Online', signal);
      await changeSensorStatus(sensor.id, 'Offline', signal);
      publish('Offline transition accepted; checking HashSet membership');
      const disconnected = await getRealtimeActiveConditions(signal);
      const issue = disconnected.activeConditions.find(
        (condition) =>
          condition.sensorProfileId === sensor.id &&
          condition.conditionType === 'SensorDisconnected',
      );
      if (!issue) throw new Error('The Offline transition did not activate SensorDisconnected.');

      await changeSensorStatus(sensor.id, 'Online', signal);
      const online = await getRealtimeActiveConditions(signal);
      if (
        online.activeConditions.some(
          (condition) =>
            condition.sensorProfileId === sensor.id &&
            condition.conditionType === 'SensorDisconnected',
        )
      ) {
        throw new Error('Online recovery did not remove the active disconnect condition.');
      }
      verifiedObservations.push(
        'Online to Offline created SensorDisconnected in the authoritative HashSet.',
      );
      verifiedObservations.push(
        'Returning Online cleared that condition without discarding resolved Part 1 history.',
      );
      break;
    }

    case 'priority': {
      // The bypass proof requires an idle worker. It does not pause traffic
      // owned by another scenario or hide already-in-flight operations.
      if (before.pendingCount !== 0 || before.outstandingDispatchedCount !== 0) {
        throw new Error(
          'Critical bypass requires an idle worker and empty queues. Wait or refresh first.',
        );
      }
      const sensor = await prepare('priority', 'Integer');
      await changeSensorStatus(sensor.id, 'Online', signal);
      const base = Date.now() - 120_000;
      let pauseAttempted = false;
      let resumeFailure: unknown;
      let scenarioFailure: unknown;

      try {
        pauseAttempted = true;
        await setOrdinaryDispatchPaused(true, signal);
        publish('Ordinary dispatch paused by this scenario; critical lane remains active');
        await send(sensor, 10, capturedAt(base, 10));
        await send(sensor, 11, capturedAt(base, 20));
        await waitFor('two queued ordinary packets', signal, async () => {
          const state = await getRealtimeProcessing(signal);
          return state.ordinaryPendingCount >= 2 ? true : null;
        });
        await send(sensor, 100, capturedAt(base, 30));

        await waitFor(
          'critical packet processed ahead of both ordinary packets',
          signal,
          async () => {
            const state = await getRealtimeProcessing(signal);
            const device = state.recentDispatches.filter(
              (entry) => entry.telemetry.sensorProfileId === sensor.id,
            );
            const critical = device.find(
              (entry) =>
                entry.telemetry.integerValue === 100 &&
                entry.lane === 'Critical' &&
                entry.outcome === 'Processed',
            );
            return critical && device.length === 1 && state.ordinaryPendingCount >= 2 ? true : null;
          },
        );
        verifiedObservations.push(
          'Critical reading 100 processed while ordinary readings 10 and 11 remained queued.',
        );
      } catch (error) {
        // Capture the first failure so a failed global-lane restoration can
        // take precedence over an ordinary verification error or Stop.
        scenarioFailure = error;
      } finally {
        // Never pass the cancelled scenario token to the global lane restore.
        // An aborted or failed pause request may still have reached the API.
        if (pauseAttempted) {
          try {
            await setOrdinaryDispatchPaused(false);
          } catch (error) {
            resumeFailure = error;
          }
        }
      }
      if (resumeFailure) {
        throw new Error(
          'Automatic ordinary-lane restoration failed. Use Resume ordinary dispatch now.',
        );
      }
      if (scenarioFailure) throw scenarioFailure;
      publish('Ordinary lane resumed; checking the true worker dequeue order');
      const completed = await waitFor<TelemetryProcessingResponse>(
        'all three actual worker completions',
        signal,
        async () => {
          const state = await getRealtimeProcessing(signal);
          const device = state.recentDispatches.filter(
            (entry) =>
              entry.telemetry.sensorProfileId === sensor.id && entry.outcome === 'Processed',
          );
          return device.length >= 3 ? state : null;
        },
      );
      const actualOrder = completed.recentDispatches
        .filter((entry) => entry.telemetry.sensorProfileId === sensor.id)
        .sort((left, right) => left.dequeueSequence - right.dequeueSequence)
        .map((entry) => entry.telemetry.integerValue);
      if (actualOrder.join(',') !== '100,10,11') {
        throw new Error(
          `Actual dispatch order was ${actualOrder.join(', ')} rather than 100, 10, 11.`,
        );
      }
      verifiedObservations.push(
        'Verified worker dequeue order: 100 (Critical), 10 (Ordinary), 11 (Ordinary).',
      );
      break;
    }

    case 'load': {
      // One bounded typed batch POST per device, rather than 10,000 fetches.
      // Each batch contains at most 25 packets and the loop yields at every
      // awaited HTTP request; the UI can render progress and Stop remains usable.
      const { deviceCount, readingsPerDevice } = settings;
      for (let deviceIndex = 0; deviceIndex < deviceCount; deviceIndex += 1) {
        signal.throwIfAborted();
        const type: TelemetryDataType =
          deviceIndex % 3 === 0 ? 'Float' : deviceIndex % 3 === 1 ? 'Integer' : 'Boolean';
        const sensor = await prepare(`load${deviceIndex + 1}`, type);
        const base = Date.now() - (readingsPerDevice + 90) * 1_000;
        const batches: IngestTelemetryRequest[][] = [];

        for (let offset = 0; offset < readingsPerDevice; offset += batchSize) {
          const batch: IngestTelemetryRequest[] = [];
          for (
            let index = offset;
            index < Math.min(readingsPerDevice, offset + batchSize);
            index += 1
          ) {
            const value =
              type === 'Float'
                ? 20 + index / 100
                : type === 'Integer'
                  ? 1_000 + index
                  : index % 2 === 0;
            batch.push(createReading(type, value, capturedAt(base, index)));
          }
          batches.push(batch);
        }

        const accepted = await ingestTelemetryBatch(sensor.id, { batches }, signal);
        if (accepted.acceptedPacketCount !== readingsPerDevice) {
          throw new Error(
            `Sensor ${sensor.id} accepted only ${accepted.acceptedPacketCount} of ${readingsPerDevice} readings.`,
          );
        }
        counts.acceptedBatches += 1;
        counts.acceptedReadings += accepted.acceptedPacketCount;
        publish(
          `Accepted ${counts.registeredDevices}/${deviceCount} devices and ${counts.acceptedReadings} typed readings`,
        );
      }
      verifiedObservations.push(
        `${counts.acceptedReadings} typed readings confirmed by ${counts.acceptedBatches} actual batch API responses.`,
      );
      verifiedObservations.push(
        'Background processing continues after submission. Process-wide counters are observations, not per-run timings.',
      );
      break;
    }
  }

  signal.throwIfAborted();
  const after = await getRealtimeProcessing(signal);
  publish('Scenario submitted; final server processing counters retrieved');
  return {
    ...counts,
    phase: 'Completed',
    scenario,
    observedProcessAcceptedDelta: after.acceptedCount - before.acceptedCount,
    observedProcessProcessedDelta: after.processedCount - before.processedCount,
    pendingAtEnd: after.pendingCount,
    elapsedMilliseconds: Date.now() - startedAt,
    verifiedObservations,
  };
}
