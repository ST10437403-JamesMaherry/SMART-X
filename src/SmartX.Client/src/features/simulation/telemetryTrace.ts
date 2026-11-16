import { getDashboardSnapshot, type DashboardIssueResponse } from '../../api/dashboardApi';
import {
  getRealtimeHistory,
  getRealtimeProcessing,
  type RealtimeHistoryEventResponse,
} from '../../api/realtimeApi';
import { getSensors, type SensorResponse } from '../../api/sensorsApi';
import { ingestTelemetry, type TelemetryReadingResponse } from '../../api/telemetryApi';
import { loadConsistentTopology } from '../../api/topologyApi';
import { matchScenario } from './scenarios';

/**
 * The HTTP telemetry endpoint and logical mesh are independent simulations.
 * This trace intentionally does not claim the packet traversed radio links.
 */
export type TraceStep = 'baseline' | 'stable' | 'spike';

export interface TraceChoice {
  readonly id: TraceStep;
  readonly title: string;
  readonly value: number;
  readonly description: string;
}

export const traceChoices: readonly TraceChoice[] = [
  {
    id: 'baseline',
    title: 'Baseline',
    value: 20,
    description: 'Establish a reference reading for the moisture probe.',
  },
  {
    id: 'stable',
    title: 'Small change',
    value: 20.1,
    description: 'Submit a nearly unchanged reading for comparison.',
  },
  {
    id: 'spike',
    title: 'Sudden spike',
    value: 80,
    description: 'Submit a large jump and inspect the server classification.',
  },
];

export interface TelemetryTraceResult {
  readonly accepted: TelemetryReadingResponse;
  readonly processed: RealtimeHistoryEventResponse;
  readonly conditions: readonly DashboardIssueResponse[];
}

const demoIdentifiers = [
  'AA:BB:CC:EE:07:A1',
  'AA:BB:CC:EE:07:A2',
  'AA:BB:CC:EE:07:A3',
  'AA:BB:CC:EE:07:A4',
  'AA:BB:CC:EE:07:A5',
  'AA:BB:CC:EE:07:A6',
];
const traceSensorIdentifier = 'AA:BB:CC:EE:07:A3';
const pollIntervalMs = 200;
const processingTimeoutMs = 15_000;

export class TelemetryTraceError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'TelemetryTraceError';
  }
}

/** Refuse arbitrary registries and avoid silently generating new demo devices. */
async function findDemoProbe(signal: AbortSignal): Promise<SensorResponse> {
  const [projection, registry] = await Promise.all([
    loadConsistentTopology(null, signal),
    getSensors(signal),
  ]);
  const scenario = matchScenario(projection);
  if (scenario === null || (scenario.id !== 'healthy' && scenario.id !== 'recovered')) {
    throw new TelemetryTraceError(
      'Load the healthy greenhouse scenario first. Telemetry Trace does not modify the mesh.',
    );
  }

  const expected = new Set(demoIdentifiers.map((id) => id.toLowerCase()));
  if (
    registry.length !== expected.size ||
    new Set(registry.map((sensor) => sensor.deviceIdentifier.toLowerCase())).size !== expected.size ||
    !registry.every(
      (sensor) =>
        expected.has(sensor.deviceIdentifier.toLowerCase()) &&
        sensor.status === 'Online' &&
        sensor.telemetryDataType === 'Float',
    )
  ) {
    throw new TelemetryTraceError(
      'Telemetry Trace only operates on the unchanged six-device greenhouse fixture.',
    );
  }

  const probe = registry.find(
    (sensor) => sensor.deviceIdentifier.toLowerCase() === traceSensorIdentifier.toLowerCase(),
  );
  if (
    !probe ||
    probe.name !== 'West-zone moisture probe' ||
    probe.category !== 'Environmental' ||
    probe.deploymentLocation.facility !== 'Greenhouse' ||
    probe.deploymentLocation.zone !== 'Hydroponics' ||
    probe.deploymentLocation.nodeId !== 'Zone-A3'
  ) {
    throw new TelemetryTraceError('The expected demo probe is missing or has been modified.');
  }
  return probe;
}

function isMatchingCompletion(
  event: RealtimeHistoryEventResponse,
  accepted: TelemetryReadingResponse,
): boolean {
  return (
    event.kind === 'TelemetryProcessed' &&
    event.targetDeviceId.toLowerCase() === accepted.sensorProfileId.toLowerCase() &&
    event.telemetry !== null &&
    event.telemetry.sensorProfileId.toLowerCase() === accepted.sensorProfileId.toLowerCase() &&
    event.telemetry.telemetryDataType === 'Float' &&
    event.telemetry.floatValue === accepted.floatValue &&
    Date.parse(event.telemetry.capturedAtUtc) === Date.parse(accepted.capturedAtUtc)
  );
}

async function awaitProcessing(
  accepted: TelemetryReadingResponse,
  signal: AbortSignal,
): Promise<RealtimeHistoryEventResponse> {
  const deadline = Date.now() + processingTimeoutMs;
  while (Date.now() < deadline) {
    signal.throwIfAborted();
    const history = await getRealtimeHistory(accepted.sensorProfileId, 100, 'desc', signal);
    const event = history.events.find((entry) => isMatchingCompletion(entry, accepted));
    if (event) return event;

    await new Promise<void>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        signal.removeEventListener('abort', abort);
        resolve();
      }, pollIntervalMs);
      const abort = () => {
        window.clearTimeout(timer);
        reject(new DOMException('Telemetry trace cancelled.', 'AbortError'));
      };
      signal.addEventListener('abort', abort, { once: true });
      if (signal.aborted) abort();
    });
  }
  throw new TelemetryTraceError(
    'The API accepted the reading, but no matching worker-history completion arrived within 15 seconds. Check Operations before retrying.',
  );
}

/**
 * An HTTP 201 receipt does not prove that the background worker completed.
 * The UI publishes it separately, then checks a device-specific event.
 */
export async function runTelemetryTrace(
  step: TraceStep,
  signal: AbortSignal,
  onAccepted: (reading: TelemetryReadingResponse) => void,
): Promise<TelemetryTraceResult> {
  const choice = traceChoices.find((item) => item.id === step);
  if (!choice) throw new TelemetryTraceError('Unsupported trace step.');

  const probe = await findDemoProbe(signal);
  const processing = await getRealtimeProcessing(signal);
  if (
    processing.failure !== null ||
    processing.workerStatus === 'Faulted' ||
    processing.workerStatus === 'Stopped' ||
    processing.ordinaryDispatchPaused
  ) {
    throw new TelemetryTraceError(
      'The telemetry worker is unavailable or ordinary dispatch is paused. Inspect Operations first.',
    );
  }

  signal.throwIfAborted();
  const accepted = await ingestTelemetry(
    probe.id,
    {
      capturedAtUtc: new Date().toISOString(),
      floatValue: choice.value,
      integerValue: null,
      booleanValue: null,
    },
    signal,
  );
  if (
    accepted.sensorProfileId.toLowerCase() !== probe.id.toLowerCase() ||
    accepted.telemetryDataType !== 'Float' ||
    accepted.floatValue !== choice.value
  ) {
    throw new TelemetryTraceError('The HTTP receipt did not match the submitted Float reading.');
  }
  if (!signal.aborted) onAccepted(accepted);
  const processed = await awaitProcessing(accepted, signal);
  signal.throwIfAborted();
  const dashboard = await getDashboardSnapshot(false, signal);
  return {
    accepted,
    processed,
    conditions: dashboard.issues.filter(
      (issue) =>
        issue.sensorProfileId.toLowerCase() === probe.id.toLowerCase() &&
        (issue.type === 'TelemetrySpike' || issue.type === 'RepeatedFailure'),
    ),
  };
}
