import { useEffect, useRef, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  executeRelayCommand,
  getActuatorCommandHistory,
  undoRelayCommand,
  type ActuatorCommandHistoryResponse,
  type RelayCommandType,
} from '../../api/commandsApi';
import type { RealtimeDeviceResponse } from '../../api/realtimeApi';

interface RealtimeCommandPanelProps {
  readonly device: RealtimeDeviceResponse;
  /** Incremented only after the parent receives a real HTTP snapshot. */
  readonly refreshToken: number;
  /** Refresh the authoritative device and processing data after an accepted command. */
  readonly onCommandCommitted: () => void;
}

/**
 * Real per-device relay controls. The server's Stack is the only Undo source:
 * no browser-side history or optimistic relay-state mutation is performed.
 *
 * The history GET is intentionally permitted for offline actuators; mutating
 * controls are disabled unless the registered sensor is currently Online.
 */
export default function RealtimeCommandPanel({
  device,
  refreshToken,
  onCommandCommitted,
}: RealtimeCommandPanelProps) {
  const [history, setHistory] = useState<ActuatorCommandHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<string | null>(null);
  const [busy, setBusy] = useState<RelayCommandType | 'Undo' | null>(null);
  const [reloadSequence, setReloadSequence] = useState(0);
  const actionInFlightRef = useRef(false);

  const supported = device.commandedActuator !== null;

  // A keyed parent remount prevents history from an earlier selected device
  // appearing under a newly selected sensor. Cancellation prevents stale GETs.
  useEffect(() => {
    if (!supported) return;

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      async function load(): Promise<void> {
        setLoading(true);
        setLoadError(null);

        try {
          const result = await getActuatorCommandHistory(device.id, controller.signal);
          if (!controller.signal.aborted) setHistory(result);
        } catch (error) {
          if (!controller.signal.aborted) {
            setLoadError(
              getApiErrorMessage(error, 'Unable to retrieve this actuator command audit.'),
            );
          }
        } finally {
          if (!controller.signal.aborted) setLoading(false);
        }
      }
      void load();
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [device.id, refreshToken, reloadSequence, supported]);

  /** Commit a command by HTTP and then request fresh authoritative projections. */
  async function execute(type: RelayCommandType): Promise<void> {
    if (actionInFlightRef.current || !supported || device.status !== 'Online') return;

    actionInFlightRef.current = true;
    setBusy(type);
    setActionError(null);
    setLastResult(null);

    try {
      const result = await executeRelayCommand(device.id, type);
      setLastResult(
        result.command.stateChanged
          ? `${type} accepted: ${result.command.previousState} to ${result.command.resultingState}.`
          : `${type} accepted as a no-op. Undo Stack depth was not increased.`,
      );
      // The result is server-confirmed; refresh command audit and device state.
      setReloadSequence((current) => current + 1);
      onCommandCommitted();
    } catch (error) {
      setActionError(getApiErrorMessage(error, 'Unable to execute the relay command.'));
      // Another client may have changed device eligibility or Stack state.
      setReloadSequence((current) => current + 1);
      onCommandCommitted();
    } finally {
      actionInFlightRef.current = false;
      setBusy(null);
    }
  }

  /**
   * Supply exactly the command ID reported at the top of the backend Stack.
   * A stale ID produces HTTP 409; we display the ProblemDetails and re-fetch.
   */
  async function undo(): Promise<void> {
    const current = history?.actuator ?? device.commandedActuator;
    const expectedId = current?.latestUndoableCommandId;

    if (actionInFlightRef.current || !supported || device.status !== 'Online' || !expectedId) {
      return;
    }

    actionInFlightRef.current = true;
    setBusy('Undo');
    setActionError(null);
    setLastResult(null);

    try {
      const result = await undoRelayCommand(device.id, expectedId);
      setLastResult(
        `Undo confirmed: restored ${result.command.resultingState} from the original command.`,
      );
      setReloadSequence((current) => current + 1);
      onCommandCommitted();
    } catch (error) {
      setActionError(getApiErrorMessage(error, 'Unable to undo the latest relay command.'));
      setReloadSequence((current) => current + 1);
      onCommandCommitted();
    } finally {
      actionInFlightRef.current = false;
      setBusy(null);
    }
  }

  const authoritativeState = history?.actuator ?? device.commandedActuator;
  const enabled = supported && device.status === 'Online' && busy === null && !loading;
  const canUndo = enabled && !!authoritativeState?.latestUndoableCommandId;

  return (
    <section className="realtime-section" aria-labelledby="realtime-command-title">
      <header className="realtime-section-header">
        <p className="realtime-kicker">04 / REAL ACTUATOR CONTROL</p>
        <h3 id="realtime-command-title">Actuator commands</h3>
        <p className="realtime-section-detail">
          Commands execute over HTTP. Undo uses the real per-device Stack.
        </p>
      </header>

      {!supported ? (
        <p className="realtime-empty">
          This sensor is not an Actuator with Boolean telemetry. Select a supported device to
          operate a relay.
        </p>
      ) : (
        <>
          <dl className="realtime-facts realtime-command-facts">
            <div>
              <dt>Commanded state</dt>
              <dd data-testid="realtime-commanded-state">
                {authoritativeState?.commandedState ?? 'Unavailable'}
              </dd>
            </div>
            <div>
              <dt>Undo Stack depth</dt>
              <dd data-testid="realtime-undo-depth">{authoritativeState?.undoDepth ?? 0}</dd>
            </div>
            <div>
              <dt>Accepted command audit</dt>
              <dd>{history?.totalAuditCount ?? (loading ? 'Loading' : 'Unavailable')}</dd>
            </div>
          </dl>

          <div className="realtime-button-row" aria-label="Relay command controls">
            <button
              type="button"
              className="realtime-action realtime-action--primary"
              disabled={!enabled}
              onClick={() => void execute('RelayOn')}
            >
              {busy === 'RelayOn' ? 'Sending...' : 'Relay On'}
            </button>
            <button
              type="button"
              className="realtime-action"
              disabled={!enabled}
              onClick={() => void execute('RelayOff')}
            >
              {busy === 'RelayOff' ? 'Sending...' : 'Relay Off'}
            </button>
            <button
              type="button"
              className="realtime-action"
              disabled={!canUndo}
              onClick={() => void undo()}
            >
              {busy === 'Undo' ? 'Undoing...' : 'Undo latest command'}
            </button>
          </div>

          {device.status !== 'Online' && (
            <p className="realtime-explainer">
              Commands are disabled while the registered sensor is {device.status}. You can still
              inspect its existing audit.
            </p>
          )}
          {loading && <p className="realtime-explainer">Refreshing command audit...</p>}
          {loadError && (
            <div className="realtime-inline-error" role="alert">
              <p>{loadError}</p>
              <button
                type="button"
                className="realtime-action"
                onClick={() => setReloadSequence((current) => current + 1)}
              >
                Retry command audit
              </button>
            </div>
          )}
          {actionError && (
            <p className="realtime-inline-error" role="alert">
              {actionError}
            </p>
          )}
          {lastResult && (
            <p className="realtime-confirmation" role="status">
              {lastResult}
            </p>
          )}

          {history && history.recentAudit.length > 0 && (
            <div className="realtime-audit">
              <h4>Recent accepted commands</h4>
              <ol>
                {history.recentAudit
                  .slice(-5)
                  .reverse()
                  .map((record) => (
                    <li key={record.commandId}>
                      <span>
                        {record.isReversal
                          ? 'Undo reversal'
                          : record.stateChanged
                            ? record.commandType
                            : `${record.commandType} no-op`}
                      </span>
                      <span>
                        {record.previousState} → {record.resultingState}
                      </span>
                    </li>
                  ))}
              </ol>
            </div>
          )}
        </>
      )}
    </section>
  );
}
