import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import { getApiErrorMessage } from '../../api/apiTypes';
import { recordOperatorView } from '../../api/operatorInteractionsApi';
import {
  getRealtimeActiveConditions,
  getRealtimeSnapshot,
  type RealtimeActiveConditionsResponse,
  type RealtimeDeviceResponse,
  type RealtimeSnapshotResponse,
  type TelemetryDispatchResponse,
} from '../../api/realtimeApi';
import type { TelemetryReadingResponse } from '../../api/telemetryApi';
import { routes } from '../../routing/routes';
import { useWorkspaceState } from '../../state/workspaceState';
import DeviceSearch from './DeviceSearch';
import RealtimeCommandPanel from './RealtimeCommandPanel';
import RealtimeHistoryPanel from './RealtimeHistoryPanel';
import RealtimeSimulator from './RealtimeSimulator';
import { useRealtimeStream, type RealtimeConnectionState } from './useRealtimeStream';
import './RealtimeWorkspace.css';

type ViewState = 'loading' | 'ready' | 'error';

const invalidationWindowMs = 150;
const visibleDispatchCount = 8;

function formatTime(timestamp: string | null): string {
  if (!timestamp) return 'Not recorded';
  const instant = new Date(timestamp);
  if (Number.isNaN(instant.getTime())) return timestamp;

  return new Intl.DateTimeFormat('en-ZA', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(instant);
}

/** Preserve real zero and false values; neither means "no observation". */
function formatReading(reading: TelemetryReadingResponse | null): string {
  if (!reading) return 'Awaiting processing';
  switch (reading.telemetryDataType) {
    case 'Float':
      return reading.floatValue === null ? 'Unavailable' : String(reading.floatValue);
    case 'Integer':
      return reading.integerValue === null ? 'Unavailable' : String(reading.integerValue);
    case 'Boolean':
      return reading.booleanValue === null ? 'Unavailable' : String(reading.booleanValue);
  }
}

function formatCount(value: number): string {
  return value.toLocaleString('en-ZA');
}

function connectionLabel(state: RealtimeConnectionState): string {
  switch (state) {
    case 'connected':
      return 'SignalR connected';
    case 'connecting':
      return 'SignalR connecting';
    case 'reconnecting':
      return 'SignalR reconnecting';
    case 'disconnected':
      return 'SignalR disconnected';
  }
}

function describeDispatch(dispatch: TelemetryDispatchResponse): string {
  const value = formatReading(dispatch.telemetry);
  return `${dispatch.telemetry.telemetryDataType} ${value}`;
}

/**
 * P2-07 operator console, backed entirely by HTTP snapshots and real API
 * mutations. SignalR transports only invalidations, never mutable device data.
 *
 * Important safeguards:
 * - Coalesce notification bursts before starting HTTP work.
 * - Abort superseded reads and ignore late/stale completions.
 * - Preserve the last successful snapshot during network/SignalR outages.
 * - Refresh after hub reconnect and after accepted command operations.
 * - Keep safe selection/search context in the existing application-wide provider.
 */
function RealtimeWorkspace() {
  const { selectedSensorId, setSelectedSensorId, setSourceAlertEpisodeId } = useWorkspaceState();

  const [snapshot, setSnapshot] = useState<RealtimeSnapshotResponse | null>(null);
  const [conditions, setConditions] = useState<RealtimeActiveConditionsResponse | null>(null);
  const [viewState, setViewState] = useState<ViewState>('loading');
  const [initialError, setInitialError] = useState<string | null>(null);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);
  const [operatorViewError, setOperatorViewError] = useState<string | null>(null);

  const requestRef = useRef<AbortController | null>(null);
  const requestNumberRef = useRef(0);
  const hasSnapshotRef = useRef(false);
  const invalidationTimerRef = useRef<number | null>(null);

  /** Reload device and active-condition projections as one UI update. */
  const refreshSnapshot = useCallback(async (): Promise<void> => {
    requestRef.current?.abort();
    const controller = new AbortController();
    const requestNumber = ++requestNumberRef.current;
    requestRef.current = controller;

    setRefreshing(true);
    setRefreshError(null);

    try {
      const [nextSnapshot, nextConditions] = await Promise.all([
        getRealtimeSnapshot(controller.signal),
        getRealtimeActiveConditions(controller.signal),
      ]);

      if (controller.signal.aborted || requestNumberRef.current !== requestNumber) return;

      setSnapshot(nextSnapshot);
      setConditions(nextConditions);
      setViewState('ready');
      setInitialError(null);
      hasSnapshotRef.current = true;
      // This token makes the selected device panels reload their own HTTP
      // histories without using the worker's bounded diagnostic records.
      setRefreshToken((current) => current + 1);
    } catch (error) {
      if (controller.signal.aborted || requestNumberRef.current !== requestNumber) return;

      const message = getApiErrorMessage(
        error,
        'Unable to load the Smart-X real-time snapshot. Check the API and try again.',
      );

      if (!hasSnapshotRef.current) {
        setInitialError(message);
        setViewState('error');
      } else {
        setRefreshError(message);
      }
    } finally {
      if (requestRef.current === controller) {
        requestRef.current = null;
        if (!controller.signal.aborted) setRefreshing(false);
      }
    }
  }, []);

  /**
   * Multiple worker completions can emit dozens of invalidations in a burst.
   * One fixed 150 ms window turns them into a single HTTP refresh. A new
   * window will start after that request if additional events arrive.
   */
  const scheduleRefresh = useCallback((): void => {
    if (invalidationTimerRef.current !== null) return;

    invalidationTimerRef.current = window.setTimeout(() => {
      invalidationTimerRef.current = null;
      void refreshSnapshot();
    }, invalidationWindowMs);
  }, [refreshSnapshot]);

  // The hook retries initial SignalR failures, handles reconnects, and
  // invokes scheduleRefresh after connection/reconnection and new events.
  const connectionState = useRealtimeStream(scheduleRefresh);

  useEffect(() => {
    const initialTimer = window.setTimeout(() => void refreshSnapshot(), 0);

    return () => {
      window.clearTimeout(initialTimer);
      if (invalidationTimerRef.current !== null) {
        window.clearTimeout(invalidationTimerRef.current);
        invalidationTimerRef.current = null;
      }
      requestRef.current?.abort();
      requestRef.current = null;
      requestNumberRef.current += 1;
    };
  }, [refreshSnapshot]);

  /**
   * Only an explicit operator inspection creates a DeviceViewed event.
   * Registration, initial selection, simulator selections, HTTP GETs, and
   * SignalR invalidations must never invent an operator interaction.
   */
  async function inspectDevice(deviceId: string): Promise<void> {
    setSelectedSensorId(deviceId);
    setOperatorViewError(null);
    try {
      await recordOperatorView(deviceId);
    } catch (error) {
      setOperatorViewError(
        getApiErrorMessage(error, 'Device selected, but the view could not be recorded.'),
      );
    }
  }

  /** Record the actual active issue episode before retaining its source context. */
  async function inspectAlert(deviceId: string, episodeId: string): Promise<void> {
    setSelectedSensorId(deviceId);
    setOperatorViewError(null);
    try {
      await recordOperatorView(deviceId, episodeId);
      setSourceAlertEpisodeId(episodeId);
    } catch (error) {
      setOperatorViewError(
        getApiErrorMessage(error, 'The alert view could not be recorded or confirmed.'),
      );
    }
  }

  // One selection shared with Part 1; neither the queue nor an alternative
  // registry is recreated in browser state.
  const selectedDevice: RealtimeDeviceResponse | null =
    snapshot?.devices.find((device) => device.id === selectedSensorId) ??
    snapshot?.devices[0] ??
    null;

  const selectedConditions =
    conditions?.activeConditions.filter((item) => item.sensorProfileId === selectedDevice?.id) ??
    [];

  const processing = snapshot?.processing;
  const recentDispatches =
    processing?.recentDispatches.slice(-visibleDispatchCount).reverse() ?? [];

  return (
    <section className="realtime-workspace" aria-labelledby="realtime-workspace-title">
      <header className="realtime-workspace-heading">
        <div>
          <p className="realtime-kicker">OPERATIONS / SERVER STATE</p>
          <h2 id="realtime-workspace-title">Operations console</h2>
          <p className="realtime-subtitle">
            Live queue processing, independent relay commands and timestamp-ordered device history.
            HTTP remains authoritative when SignalR is unavailable.
          </p>
        </div>
        <div className="realtime-refresh-controls">
          <span
            className={`realtime-connectivity realtime-connectivity--${connectionState}`}
            role="status"
            aria-live="polite"
          >
            {connectionLabel(connectionState)}
          </span>
          <span className="realtime-read-status" role="status" aria-live="polite">
            {refreshing
              ? 'Refreshing server state'
              : snapshot
                ? 'HTTP snapshot ready'
                : 'Awaiting first snapshot'}
          </span>
          <button
            type="button"
            className="realtime-action"
            disabled={refreshing}
            onClick={() => void refreshSnapshot()}
          >
            {refreshing ? 'Refreshing...' : 'Refresh snapshot'}
          </button>
        </div>
      </header>

      {viewState === 'loading' && (
        <p className="realtime-message" role="status">
          Loading registered devices and actual processing diagnostics...
        </p>
      )}

      {viewState === 'error' && (
        <div className="realtime-message realtime-message--error" role="alert">
          <p>{initialError}</p>
          <button type="button" className="realtime-action" onClick={() => void refreshSnapshot()}>
            Retry snapshot
          </button>
        </div>
      )}

      {snapshot && processing && viewState === 'ready' && (
        <>
          <p className="realtime-snapshot-time">
            Snapshot received{' '}
            <time dateTime={snapshot.generatedAtUtc}>{formatTime(snapshot.generatedAtUtc)}</time>
            {' · '}Process-wide counters
          </p>

          <div className="realtime-metrics" aria-label="Process-wide telemetry counters">
            <div className="realtime-metric">
              <span>Registered devices</span>
              <strong>{formatCount(snapshot.deviceCount)}</strong>
            </div>
            <div className="realtime-metric">
              <span>Accepted packets</span>
              <strong>{formatCount(processing.acceptedCount)}</strong>
            </div>
            <div className="realtime-metric">
              <span>Processed packets</span>
              <strong>{formatCount(processing.processedCount)}</strong>
            </div>
            <div className="realtime-metric">
              <span>Pending packets</span>
              <strong>{formatCount(processing.pendingCount)}</strong>
            </div>
          </div>

          {processing.failure && (
            <p className="realtime-message realtime-message--error" role="alert">
              Worker failure reported: {processing.failure}
            </p>
          )}

          <DeviceSearch
            refreshToken={refreshToken}
            activeConditions={conditions?.activeConditions ?? []}
            onInspectDevice={inspectDevice}
            onInspectAlert={inspectAlert}
            onSelectAssociatedDevice={setSelectedSensorId}
          />

          {operatorViewError && (
            <p className="realtime-inline-error" role="alert">
              {operatorViewError}
            </p>
          )}

          <div className="realtime-columns">
            <section className="realtime-section" aria-labelledby="realtime-device-title">
              <header className="realtime-section-header">
                <p className="realtime-kicker">01 / DEVICE CONTEXT</p>
                <h3 id="realtime-device-title">Selected device</h3>
              </header>

              {selectedDevice ? (
                <>
                  <label className="realtime-label" htmlFor="realtime-device-select">
                    Selected device
                  </label>
                  <select
                    id="realtime-device-select"
                    className="realtime-device-select"
                    value={selectedDevice.id}
                    onChange={(event) => void inspectDevice(event.target.value)}
                  >
                    {snapshot.devices.map((device) => (
                      <option key={device.id} value={device.id}>
                        {device.name} — {device.deviceIdentifier}
                      </option>
                    ))}
                  </select>

                  <h4 className="realtime-device-name">{selectedDevice.name}</h4>
                  <p className="realtime-device-identifier">{selectedDevice.deviceIdentifier}</p>

                  <dl className="realtime-facts">
                    <div>
                      <dt>Status</dt>
                      <dd>{selectedDevice.status}</dd>
                    </div>
                    <div>
                      <dt>Category</dt>
                      <dd>{selectedDevice.category}</dd>
                    </div>
                    <div>
                      <dt>Payload type</dt>
                      <dd>{selectedDevice.telemetryDataType}</dd>
                    </div>
                    <div>
                      <dt>Latest processed value</dt>
                      <dd>{formatReading(selectedDevice.latestProcessedTelemetry)}</dd>
                    </div>
                    <div>
                      <dt>Processed at</dt>
                      <dd>{formatTime(selectedDevice.lastProcessedAtUtc)}</dd>
                    </div>
                    <div>
                      <dt>Commanded relay</dt>
                      <dd>
                        {selectedDevice.commandedActuator?.commandedState ?? 'Not applicable'}
                      </dd>
                    </div>
                    {selectedDevice.commandedActuator && (
                      <div>
                        <dt>Undo Stack depth</dt>
                        <dd>{selectedDevice.commandedActuator.undoDepth}</dd>
                      </div>
                    )}
                  </dl>
                  <p className="realtime-explainer">
                    Latest processed telemetry is separate from latest accepted traffic, operational
                    sensor status and simulated commanded relay state.
                  </p>
                </>
              ) : (
                <div className="realtime-empty">
                  <p>No devices registered in this API session.</p>
                  <Link to={routes.ingestion}>Open sensor ingestion</Link>
                </div>
              )}
            </section>

            <section className="realtime-section" aria-labelledby="realtime-queue-title">
              <header className="realtime-section-header">
                <p className="realtime-kicker">02 / REAL PROCESSING</p>
                <h3 id="realtime-queue-title">Dispatch lanes</h3>
              </header>
              <dl className="realtime-facts">
                <div>
                  <dt>Worker status</dt>
                  <dd>{processing.workerStatus}</dd>
                </div>
                <div>
                  <dt>Ordinary FIFO pending</dt>
                  <dd>{formatCount(processing.ordinaryPendingCount)}</dd>
                </div>
                <div>
                  <dt>Critical priority pending</dt>
                  <dd>{formatCount(processing.criticalPendingCount)}</dd>
                </div>
                <div>
                  <dt>Dispatched total</dt>
                  <dd>{formatCount(processing.dispatchedCount)}</dd>
                </div>
                <div>
                  <dt>Uncompleted dispatches</dt>
                  <dd>{formatCount(processing.outstandingDispatchedCount)}</dd>
                </div>
                <div>
                  <dt>Ordinary simulation lane</dt>
                  <dd>{processing.ordinaryDispatchPaused ? 'Paused' : 'Running'}</dd>
                </div>
              </dl>
              <p className="realtime-explainer">
                Worker counts include all devices. Ordinary-lane pausing is a simulation control;
                the critical lane remains live. Scenario controls arrive in Commit 3.
              </p>
            </section>
          </div>

          <section
            className="realtime-section realtime-dispatches"
            aria-labelledby="realtime-dispatches-title"
          >
            <header className="realtime-section-header">
              <p className="realtime-kicker">03 / QUEUE COMPLETIONS</p>
              <h3 id="realtime-dispatches-title">Recent worker dispatches</h3>
              <p className="realtime-section-detail">
                The last {visibleDispatchCount} entries in the server's bounded 100-dispatch
                diagnostic window.
              </p>
            </header>
            {recentDispatches.length === 0 ? (
              <p className="realtime-empty">No worker dispatches recorded yet.</p>
            ) : (
              <div className="realtime-table-scroll">
                <table className="realtime-table" aria-label="Recent worker dispatches">
                  <thead>
                    <tr>
                      <th scope="col">Dequeue</th>
                      <th scope="col">Lane</th>
                      <th scope="col">Outcome</th>
                      <th scope="col">Typed value</th>
                      <th scope="col">Device</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recentDispatches.map((dispatch) => (
                      <tr key={dispatch.dequeueSequence}>
                        <td>{dispatch.dequeueSequence}</td>
                        <td>
                          {dispatch.lane} / {dispatch.severity}
                        </td>
                        <td>{dispatch.outcome}</td>
                        <td>{describeDispatch(dispatch)}</td>
                        <td className="realtime-device-id-cell">
                          {snapshot.devices.find((d) => d.id === dispatch.telemetry.sensorProfileId)
                            ?.name ?? dispatch.telemetry.deviceIdentifier}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          {selectedDevice && (
            <div className="realtime-columns realtime-operator-panels">
              <RealtimeCommandPanel
                key={`command-${selectedDevice.id}`}
                device={selectedDevice}
                refreshToken={refreshToken}
                onCommandCommitted={() => void refreshSnapshot()}
              />
              <RealtimeHistoryPanel
                key={`history-${selectedDevice.id}`}
                deviceId={selectedDevice.id}
                refreshToken={refreshToken}
              />
            </div>
          )}

          {/* Real HTTP demonstration scenarios; no fabricated sensor or queue state. */}
          <RealtimeSimulator
            ordinaryDispatchPaused={processing.ordinaryDispatchPaused}
            onDeviceCreated={setSelectedSensorId}
            onDataChanged={() => void refreshSnapshot()}
          />

          <section
            className="realtime-section realtime-conditions"
            aria-labelledby="realtime-conditions-title"
          >
            <header className="realtime-section-header">
              <p className="realtime-kicker">07 / UNIQUE ACTIVE CONDITIONS</p>
              <h3 id="realtime-conditions-title">Active conditions</h3>
              <p className="realtime-section-detail">
                {formatCount(conditions?.activeConditionCount ?? 0)} active system-wide
                {' · '}
                {formatCount(conditions?.unacknowledgedConditionCount ?? 0)} unacknowledged
              </p>
            </header>
            {!selectedDevice ? (
              <p className="realtime-empty">Select a registered device to inspect conditions.</p>
            ) : selectedConditions.length === 0 ? (
              <p className="realtime-empty">No active conditions for this device.</p>
            ) : (
              <ol className="realtime-conditions-list">
                {selectedConditions.map((condition) => (
                  <li key={condition.membershipKey} className="realtime-condition">
                    <strong>{condition.conditionType}</strong>
                    <span>
                      {condition.severity} / {condition.state}
                    </span>
                    <span>{formatCount(condition.occurrenceCount)} occurrence(s)</span>
                    <button
                      className="realtime-action"
                      type="button"
                      onClick={() => void inspectAlert(selectedDevice.id, condition.issueId)}
                    >
                      View alert
                    </button>
                  </li>
                ))}
              </ol>
            )}
          </section>
        </>
      )}

      {refreshError && (
        <p className="realtime-message realtime-message--error" role="alert">
          Refresh failed. Last successful server snapshot retained. {refreshError}
        </p>
      )}
    </section>
  );
}

export default RealtimeWorkspace;
