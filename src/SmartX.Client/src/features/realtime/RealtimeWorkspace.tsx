import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  getRealtimeActiveConditions,
  getRealtimeSnapshot,
  type RealtimeDeviceResponse,
  type RealtimeSnapshotResponse,
  type RealtimeActiveConditionsResponse,
} from '../../api/realtimeApi';
import type { TelemetryReadingResponse } from '../../api/telemetryApi';
import { routes } from '../../routing/routes';
import { useWorkspaceState } from '../../state/workspaceState';
import './RealtimeWorkspace.css';

type ViewState = 'loading' | 'ready' | 'error';

/** Format timestamps supplied by the server without inventing local events. */
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

/** A zero or false payload is real data, not an absent observation. */
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

function formatCount(count: number): string {
  return count.toLocaleString('en-ZA');
}

/**
 * Commit 1 provides a useful read-only operator workspace from the real API.
 * Command execution, chronological history interaction and coalesced SignalR
 * refresh are integrated in Commit 2, without changing this source of truth.
 *
 * Only the selected device ID is stored across routes. The registry, queue,
 * reported telemetry, commanded state and active conditions are never faked
 * or retained as an independent client-side device store.
 */
function RealtimeWorkspace() {
  const { selectedSensorId, setSelectedSensorId } = useWorkspaceState();

  const [snapshot, setSnapshot] = useState<RealtimeSnapshotResponse | null>(null);
  const [conditions, setConditions] = useState<RealtimeActiveConditionsResponse | null>(null);
  const [viewState, setViewState] = useState<ViewState>('loading');
  const [initialError, setInitialError] = useState<string | null>(null);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const requestRef = useRef<AbortController | null>(null);
  const hasSnapshotRef = useRef(false);

  /**
   * Load both projections concurrently. Superseded requests are cancelled;
   * only the most recent request can publish state. On a later failure the
   * operator retains the last successfully loaded snapshot.
   */
  const refreshSnapshot = useCallback(async (): Promise<void> => {
    requestRef.current?.abort();
    const controller = new AbortController();
    requestRef.current = controller;
    setRefreshing(true);
    setRefreshError(null);

    try {
      const [nextSnapshot, nextConditions] = await Promise.all([
        getRealtimeSnapshot(controller.signal),
        getRealtimeActiveConditions(controller.signal),
      ]);

      if (controller.signal.aborted || requestRef.current !== controller) return;

      setSnapshot(nextSnapshot);
      setConditions(nextConditions);
      setViewState('ready');
      setInitialError(null);
      hasSnapshotRef.current = true;
    } catch (error) {
      if (controller.signal.aborted || requestRef.current !== controller) return;

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

  useEffect(() => {
    // Delay initial network-driven React state updates beyond the effect's
    // synchronous phase, matching the established Smart-X hooks convention.
    const timer = window.setTimeout(() => void refreshSnapshot(), 0);

    return () => {
      window.clearTimeout(timer);
      requestRef.current?.abort();
      requestRef.current = null;
    };
  }, [refreshSnapshot]);

  // Reuse the Part 1 selection when available. If the device was removed,
  // display an existing device without writing an imaginary selection.
  const selectedDevice: RealtimeDeviceResponse | null =
    snapshot?.devices.find((device) => device.id === selectedSensorId) ??
    snapshot?.devices[0] ??
    null;

  const selectedConditions =
    conditions?.activeConditions.filter((item) => item.sensorProfileId === selectedDevice?.id) ??
    [];

  const processing = snapshot?.processing;

  return (
    <section className="realtime-workspace" aria-labelledby="realtime-workspace-title">
      <header className="realtime-workspace-heading">
        <div>
          <p className="realtime-kicker">OPERATIONS / SERVER STATE</p>
          <h2 id="realtime-workspace-title">Operations console</h2>
          <p className="realtime-subtitle">
            Accepted traffic, completed processing and commanded relay state are separate facts.
          </p>
        </div>

        <div className="realtime-refresh-controls">
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
          Loading registered devices and live processing diagnostics...
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
            {' · '}Process-wide telemetry counters
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
                    onChange={(event) => setSelectedSensorId(event.target.value)}
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
                    Latest processed telemetry is not the last accepted reading. Relay commands
                    cannot overwrite reported Boolean telemetry or sensor status.
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
                These are actual worker/queue counts across all devices. Pausing ordinary simulation
                dispatch does not pause the critical lane or ingestion.
              </p>
            </section>
          </div>

          <section
            className="realtime-section realtime-conditions"
            aria-labelledby="realtime-conditions-title"
          >
            <header className="realtime-section-header">
              <p className="realtime-kicker">03 / UNIQUE ACTIVE CONDITIONS</p>
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
