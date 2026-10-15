import { useEffect, useRef, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { getRealtimeProcessing, setOrdinaryDispatchPaused } from '../../api/realtimeApi';
import {
  defaultLoadSettings,
  maximumLoadDevices,
  maximumLoadReadings,
  maximumReadingsPerDevice,
  runSimulation,
  type SimulationProgress,
  type SimulationResult,
  type SimulationScenario,
} from './simulationScenarios';

interface RealtimeSimulatorProps {
  /** The actual flag returned by the parent HTTP worker snapshot. */
  readonly ordinaryDispatchPaused: boolean;
  readonly onDeviceCreated: (sensorId: string) => void;
  readonly onDataChanged: () => void;
}

const scenarios: readonly {
  id: SimulationScenario;
  title: string;
  description: string;
}[] = [
  {
    id: 'normal',
    title: 'Normal traffic',
    description: 'Create a Float sensor and process three stable readings.',
  },
  {
    id: 'spike',
    title: 'Numeric spike',
    description: 'Trigger escalation, repeated failure, two-reading recovery and recurrence.',
  },
  {
    id: 'actuator',
    title: 'Boolean actuator',
    description: 'Register an actuator, report Boolean telemetry, issue RelayOn/no-op/Undo.',
  },
  {
    id: 'disconnect',
    title: 'Disconnect / recover',
    description: 'Verify Online → Offline → Online clears the active HashSet key.',
  },
  {
    id: 'priority',
    title: 'Critical bypass',
    description: 'Hold ordinary packets, process a critical packet first, restore dispatch.',
  },
];

function formatNumber(value: number): string {
  return value.toLocaleString('en-ZA');
}

/**
 * A UI for existing API operations. It does not own device, queue, condition,
 * worker, or command state. Run progress counts are HTTP confirmations only.
 *
 * Stop aborts further browser requests, not telemetry already accepted by
 * the server. The priority scenario restores the global ordinary lane even
 * when the operator stops the run or a verification step fails.
 */
export default function RealtimeSimulator({
  ordinaryDispatchPaused,
  onDeviceCreated,
  onDataChanged,
}: RealtimeSimulatorProps) {
  const [loadDevicesInput, setLoadDevicesInput] = useState(String(defaultLoadSettings.deviceCount));
  const [loadReadingsInput, setLoadReadingsInput] = useState(
    String(defaultLoadSettings.readingsPerDevice),
  );
  const [runningScenario, setRunningScenario] = useState<SimulationScenario | null>(null);
  const [progress, setProgress] = useState<SimulationProgress | null>(null);
  const [result, setResult] = useState<SimulationResult | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pauseBusy, setPauseBusy] = useState(false);
  const [pauseError, setPauseError] = useState<string | null>(null);
  const operationRef = useRef<AbortController | null>(null);
  const mountedRef = useRef(false);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      // Component unmount must not leave an extended simulation issuing requests.
      operationRef.current?.abort();
    };
  }, []);

  const loadDeviceCount = Number(loadDevicesInput);
  const loadReadingsPerDevice = Number(loadReadingsInput);
  const loadTarget = loadDeviceCount * loadReadingsPerDevice;
  const validLoad =
    loadDevicesInput.trim() !== '' &&
    loadReadingsInput.trim() !== '' &&
    Number.isInteger(loadDeviceCount) &&
    Number.isInteger(loadReadingsPerDevice) &&
    loadDeviceCount >= 1 &&
    loadDeviceCount <= maximumLoadDevices &&
    loadReadingsPerDevice >= 1 &&
    loadReadingsPerDevice <= maximumReadingsPerDevice &&
    loadTarget <= maximumLoadReadings;

  async function begin(scenario: SimulationScenario): Promise<void> {
    if (operationRef.current || pauseBusy || (scenario === 'load' && !validLoad)) return;

    const controller = new AbortController();
    operationRef.current = controller;
    setRunningScenario(scenario);
    setProgress(null);
    setResult(null);
    setError(null);
    setMessage(null);

    let selectedFirstDevice = false;
    try {
      const complete = await runSimulation({
        scenario,
        settings: {
          deviceCount: loadDeviceCount,
          readingsPerDevice: loadReadingsPerDevice,
        },
        signal: controller.signal,
        onProgress: (updated) => {
          if (!mountedRef.current || operationRef.current !== controller) return;
          setProgress(updated);
          // One app-wide sensor ID is preserved through existing navigation.
          // Do not repeatedly change selection during a 100-device load run.
          if (updated.firstDeviceId && !selectedFirstDevice) {
            selectedFirstDevice = true;
            onDeviceCreated(updated.firstDeviceId);
          }
        },
      });
      if (!mountedRef.current) return;
      setResult(complete);
      setMessage('Scenario completed. All displayed confirmations came from the real API.');
    } catch (reason) {
      if (!mountedRef.current) return;
      if (
        reason instanceof Error &&
        reason.message.startsWith('Automatic ordinary-lane restoration failed')
      ) {
        // Restoration failures override the ordinary Stop confirmation: the
        // shared queue may still be paused and needs explicit operator action.
        setError(reason.message);
      } else if (controller.signal.aborted) {
        setMessage(
          'Scenario stopped. Successfully accepted requests remain in Smart-X and may still be processing.',
        );
      } else {
        setError(
          reason instanceof Error && reason.name !== 'ApiError'
            ? reason.message
            : getApiErrorMessage(reason, 'The scenario could not complete.'),
        );
      }
    } finally {
      operationRef.current = null;
      if (mountedRef.current) {
        setRunningScenario(null);
        // Retrieve real counters and updated device state once the run settles.
        onDataChanged();
      }
    }
  }

  /**
   * Manual pause/resume is deliberately separate from the critical-bypass
   * scenario's scoped pause. It operates on the authoritative current flag.
   */
  async function toggleOrdinaryDispatch(): Promise<void> {
    if (operationRef.current || pauseBusy) return;
    setPauseBusy(true);
    setPauseError(null);
    try {
      const current = await getRealtimeProcessing();
      await setOrdinaryDispatchPaused(!current.ordinaryDispatchPaused);
      onDataChanged();
    } catch (reason) {
      setPauseError(getApiErrorMessage(reason, 'Could not update ordinary dispatch.'));
    } finally {
      if (mountedRef.current) setPauseBusy(false);
    }
  }

  const busy = runningScenario !== null || pauseBusy;

  return (
    <section
      className="realtime-section realtime-simulator"
      aria-labelledby="realtime-simulator-title"
    >
      <header className="realtime-section-header">
        <p className="realtime-kicker">06 / API-DRIVEN IOT SIMULATION</p>
        <h3 id="realtime-simulator-title">IoT traffic simulator</h3>
        <p className="realtime-section-detail">
          Creates clearly labelled demonstration sensors. Every status, packet, command and
          configuration flows through the existing Smart-X API.
        </p>
      </header>

      <div className="realtime-simulator-lanes">
        <div>
          <strong>Ordinary FIFO simulation control</strong>
          <p>
            Server reports: {ordinaryDispatchPaused ? 'Paused' : 'Running'}. Critical-priority work
            stays live while ordinary work is paused.
          </p>
        </div>
        <button
          type="button"
          className="realtime-action"
          disabled={busy}
          onClick={() => void toggleOrdinaryDispatch()}
        >
          {pauseBusy
            ? 'Updating dispatch...'
            : ordinaryDispatchPaused
              ? 'Resume ordinary dispatch'
              : 'Pause ordinary dispatch'}
        </button>
      </div>
      {pauseError && (
        <p role="alert" className="realtime-inline-error">
          {pauseError}
        </p>
      )}

      <div className="realtime-scenario-grid" aria-label="Finite IoT simulation scenarios">
        {scenarios.map((scenario) => (
          <div className="realtime-scenario" key={scenario.id}>
            <h4>{scenario.title}</h4>
            <p>{scenario.description}</p>
            <button
              type="button"
              className="realtime-action"
              disabled={busy || ordinaryDispatchPaused}
              onClick={() => void begin(scenario.id)}
            >
              {`Run ${scenario.title.toLowerCase()}`}
            </button>
          </div>
        ))}
      </div>

      <div className="realtime-load" aria-labelledby="realtime-load-title">
        <div>
          <p className="realtime-kicker">CONFIGURABLE / FINITE</p>
          <h4 id="realtime-load-title">Typed load demonstration</h4>
          <p>
            Initially 100 devices × 100 readings = 10,000 typed readings, submitted in actual
            bounded jagged batches. This is not a latency benchmark.
          </p>
        </div>
        <div className="realtime-load-controls">
          <label htmlFor="realtime-load-devices">Load devices</label>
          <input
            id="realtime-load-devices"
            type="number"
            inputMode="numeric"
            min={1}
            max={maximumLoadDevices}
            step={1}
            value={loadDevicesInput}
            disabled={busy}
            onChange={(event) => setLoadDevicesInput(event.target.value)}
          />
          <label htmlFor="realtime-load-readings">Readings per device</label>
          <input
            id="realtime-load-readings"
            type="number"
            inputMode="numeric"
            min={1}
            max={maximumReadingsPerDevice}
            step={1}
            value={loadReadingsInput}
            disabled={busy}
            onChange={(event) => setLoadReadingsInput(event.target.value)}
          />
        </div>
        <div className="realtime-load-footer">
          <span>
            {validLoad
              ? `${formatNumber(loadTarget)} planned readings`
              : 'Choose valid finite limits'}
          </span>
          <button
            type="button"
            className="realtime-action realtime-action--primary"
            disabled={busy || !validLoad || ordinaryDispatchPaused}
            onClick={() => void begin('load')}
          >
            Run finite load
          </button>
        </div>
      </div>

      {ordinaryDispatchPaused && !runningScenario && (
        <p className="realtime-explainer">
          Resume ordinary dispatch before starting another scenario. Existing queued telemetry is
          not discarded when you pause the lane.
        </p>
      )}

      {runningScenario && (
        <div className="realtime-simulator-progress" role="status" aria-live="polite">
          <div className="realtime-simulator-progress-heading">
            <strong>Running {runningScenario}</strong>
            <button
              type="button"
              className="realtime-action"
              onClick={() => operationRef.current?.abort()}
            >
              Stop active scenario
            </button>
          </div>
          <p>{progress?.phase ?? 'Starting HTTP scenario...'}</p>
        </div>
      )}

      {progress && (
        <dl className="realtime-simulator-counts" aria-label="Confirmed simulation progress">
          <div>
            <dt>Registered devices</dt>
            <dd data-testid="simulator-devices">{formatNumber(progress.registeredDevices)}</dd>
          </div>
          <div>
            <dt>HTTP-accepted readings</dt>
            <dd data-testid="simulator-accepted">{formatNumber(progress.acceptedReadings)}</dd>
          </div>
          <div>
            <dt>Accepted batch requests</dt>
            <dd>{formatNumber(progress.acceptedBatches)}</dd>
          </div>
          <div>
            <dt>Accepted commands / Undo</dt>
            <dd>{formatNumber(progress.acceptedCommands)}</dd>
          </div>
        </dl>
      )}

      {message && (
        <p role="status" className="realtime-simulator-feedback">
          {message}
        </p>
      )}
      {error && (
        <p role="alert" className="realtime-inline-error">
          {error} Confirmed submissions may remain in the running API.
        </p>
      )}

      {result && (
        <div className="realtime-simulator-result" aria-label="Verified scenario result">
          <h4>Verified HTTP result</h4>
          <p>
            {formatNumber(result.registeredDevices)} registered ·{' '}
            {formatNumber(result.acceptedReadings)} packets accepted ·{' '}
            {formatNumber(result.acceptedCommands)} commands/Undo accepted
          </p>
          <dl className="realtime-simulator-counts">
            <div>
              <dt>Process-wide acceptance delta</dt>
              <dd>{formatNumber(result.observedProcessAcceptedDelta)}</dd>
            </div>
            <div>
              <dt>Process-wide processing delta</dt>
              <dd>{formatNumber(result.observedProcessProcessedDelta)}</dd>
            </div>
            <div>
              <dt>Server pending at finish</dt>
              <dd>{formatNumber(result.pendingAtEnd)}</dd>
            </div>
          </dl>
          <p className="realtime-explainer">
            Process-wide deltas may include other clients and do not prove all accepted work has
            completed. The background worker continues after a finite load submission.
          </p>
          <ul>
            {result.verifiedObservations.map((observation) => (
              <li key={observation}>{observation}</li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
