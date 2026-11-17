import { useEffect, useRef, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import type { TelemetryReadingResponse } from '../../api/telemetryApi';
import {
  runTelemetryTrace,
  traceChoices,
  type TelemetryTraceResult,
  type TraceStep,
} from './telemetryTrace';
import './TelemetryTrace.css';

interface TelemetryTraceProps {
  readonly enabled: boolean;
  readonly scenarioBusy: boolean;
  readonly onBusyChange: (busy: boolean) => void;
}

type TracePhase = 'idle' | 'checking' | 'accepted' | 'completed' | 'error';

/**
 * A guided view of three real API stages, never a fabricated timeline:
 * POST receipt, worker-history completion, and current anomaly snapshot.
 */
export default function TelemetryTrace({
  enabled,
  scenarioBusy,
  onBusyChange,
}: TelemetryTraceProps) {
  const [phase, setPhase] = useState<TracePhase>('idle');
  const [accepted, setAccepted] = useState<TelemetryReadingResponse | null>(null);
  const [result, setResult] = useState<TelemetryTraceResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [lastStep, setLastStep] = useState<TraceStep | null>(null);
  const active = useRef<AbortController | null>(null);

  useEffect(() => {
    return () => {
      active.current?.abort();
      active.current = null;
    };
  }, []);

  async function run(step: TraceStep): Promise<void> {
    if (active.current || !enabled || scenarioBusy) return;

    const controller = new AbortController();
    active.current = controller;
    onBusyChange(true);
    setLastStep(step);
    setAccepted(null);
    setResult(null);
    setError(null);
    setPhase('checking');

    try {
      const observed = await runTelemetryTrace(step, controller.signal, (receipt) => {
        if (!controller.signal.aborted) {
          setAccepted(receipt);
          setPhase('accepted');
        }
      });
      if (!controller.signal.aborted) {
        setResult(observed);
        setPhase('completed');
      }
    } catch (reason: unknown) {
      if (!controller.signal.aborted) {
        setError(
          getApiErrorMessage(
            reason,
            reason instanceof Error ? reason.message : 'Unable to trace the telemetry reading.',
          ),
        );
        setPhase('error');
      }
    } finally {
      if (active.current === controller) {
        active.current = null;
      }
      onBusyChange(false);
    }
  }

  const busy = phase === 'checking' || phase === 'accepted';
  const canRun = enabled && !scenarioBusy && !busy;

  return (
    <section className="simulation-section telemetry-trace" aria-labelledby="telemetry-trace-title">
      <div className="simulation-section__heading">
        <div>
          <p className="simulation-kicker">STEP 04 / TELEMETRY TRACE</p>
          <h2 id="telemetry-trace-title">Follow one sensor reading</h2>
          <p>
            Send a Float reading from the West-zone moisture probe. Watch the difference between
            HTTP acceptance, actual worker processing and the detector's current condition.
          </p>
        </div>
        <span className="simulation-section__tag">REAL API</span>
      </div>

      <p className="simulation-help">
        Start with <strong>Healthy network</strong> in step 01. The three sample values are
        simulated, not physical moisture measurements. Send baseline, small change, then spike for
        the clearest demonstration. Repeating or reordering readings can change the detector's
        outcome because it evaluates real prior history.
      </p>

      <div className="telemetry-trace__choices" role="group" aria-label="Telemetry trace inputs">
        {traceChoices.map((choice) => (
          <article key={choice.id} className="telemetry-trace__choice">
            <span>FLOAT / {choice.value}</span>
            <h3>{choice.title}</h3>
            <p>{choice.description}</p>
            <button
              type="button"
              disabled={!canRun}
              onClick={() => void run(choice.id)}
              aria-label={`Send ${choice.id} reading`}
            >
              {busy && lastStep === choice.id ? 'Tracing…' : `Send ${choice.value}`}
            </button>
          </article>
        ))}
      </div>

      {!enabled && (
        <p className="telemetry-trace__notice" role="status">
          Load the healthy greenhouse scenario before tracing telemetry. This avoids changing
          unrelated devices or an incomplete network.
        </p>
      )}

      {(phase !== 'idle' || accepted !== null) && (
        <div
          className="telemetry-trace__results"
          aria-live="polite"
          data-testid="telemetry-trace-results"
        >
          <h3>Trace result</h3>
          <ol className="telemetry-trace__stages">
            <li>
              <strong>01 / API acceptance</strong>
              <p>
                {accepted
                  ? `Confirmed: Float ${accepted.floatValue} accepted at ${accepted.capturedAtUtc}.`
                  : phase === 'checking'
                    ? 'Checking fixture safety and submitting the reading…'
                    : 'No confirmed HTTP receipt.'}
              </p>
            </li>
            <li>
              <strong>02 / Background worker</strong>
              <p>
                {result
                  ? `Confirmed: ${result.processed.kind}; lane: ${result.processed.processingLane ?? 'unknown'}; severity: ${result.processed.severity ?? 'not supplied'}.`
                  : accepted
                    ? 'Accepted; waiting for the matching per-device worker-history event…'
                    : 'Waiting for an accepted packet.'}
              </p>
            </li>
            <li>
              <strong>03 / Anomaly conditions</strong>
              {result ? (
                result.conditions.length ? (
                  <ul>
                    {result.conditions.map((condition) => (
                      <li key={condition.id}>
                        {condition.type}: {condition.severity} · {condition.state} · occurrences{' '}
                        {condition.occurrenceCount}
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p>No active TelemetrySpike or RepeatedFailure condition for this probe.</p>
                )
              ) : (
                <p>Waiting for confirmed worker processing before reading current conditions.</p>
              )}
            </li>
          </ol>
          {phase === 'completed' && (
            <p className="telemetry-trace__success" role="status">
              Trace complete. All three stages were read from Smart-X's running API.
            </p>
          )}
          {error && (
            <p role="alert" className="telemetry-trace__error">
              {error}
              {accepted && ' The reading was accepted; inspect Operations before sending it again.'}
            </p>
          )}
        </div>
      )}

      <p className="simulation-help">
        The topology engine models logical links, while telemetry enters through the HTTP API. A
        disconnected graph node does not automatically stop HTTP packets. This trace demonstrates
        the actual software processing path, not RF transport.
      </p>
    </section>
  );
}
