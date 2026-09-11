import { useCallback, useEffect, useRef, useState } from 'react';

import {
  acknowledgeDashboardIssue,
  getDashboardSnapshot,
  type DashboardIssueResponse,
  type DashboardIssueTarget,
  type DashboardSnapshotResponse,
} from '../../api/dashboardApi';
import { getApiErrorMessage } from '../../api/apiTypes';
import type { SensorResponse } from '../../api/sensorsApi';
import SensorStateSimulator from './SensorStateSimulator';
import { useDashboardRealtime, type DashboardRealtimeState } from './useDashboardRealtime';
import './AdaptiveAnomalySpotlight.css';

interface AdaptiveAnomalySpotlightProps {
  readonly sensor: SensorResponse | null;

  readonly onSelectSensor: (sensorId: string) => void;

  readonly onSensorUpdated: (sensor: SensorResponse) => void;

  /**
   * Allows the parent sensor registry to refresh when SignalR indicates that
   * broader system state has changed.
   */
  readonly onRealtimeChange: () => void;
}

type SnapshotState = 'loading' | 'ready' | 'error';

/**
 * Limits the initial view so a large issue collection does not overwhelm the
 * operator.
 *
 * The backend still provides the complete prioritized collection and users
 * can explicitly expand it when necessary.
 */
const defaultVisibleIssueCount = 5;

/**
 * Returns the section containing the most useful evidence for a dashboard
 * issue.
 */
function getTargetSectionId(target: DashboardIssueTarget): string {
  if (target === 'Telemetry') {
    return 'telemetry-management';
  }

  if (target === 'Attachments') {
    return 'attachment-management';
  }

  return 'sensor-management';
}

/**
 * Formats backend issue categories for concise human-facing display.
 */
function formatIssueType(issue: DashboardIssueResponse): string {
  switch (issue.type) {
    case 'TelemetrySpike':
      return 'Telemetry spike';

    case 'SensorDisconnected':
      return 'Sensor disconnected';

    case 'RepeatedFailure':
      return 'Repeated failure';

    case 'ConfigurationIssue':
      return 'Configuration';

    case 'SensorStateChange':
      return 'Sensor state';

    default:
      return issue.type;
  }
}

/**
 * Formats one UTC timestamp for local operator display.
 */
function formatTimestamp(timestamp: string): string {
  const parsedTimestamp = new Date(timestamp);

  if (Number.isNaN(parsedTimestamp.getTime())) {
    return timestamp;
  }

  return new Intl.DateTimeFormat('en-ZA', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(parsedTimestamp);
}

/**
 * Returns concise text representing the live SignalR state.
 */
function formatRealtimeState(state: DashboardRealtimeState): string {
  switch (state) {
    case 'connected':
      return 'Live';

    case 'reconnecting':
      return 'Reconnecting';

    case 'connecting':
      return 'Connecting';

    case 'disconnected':
      return 'Offline';

    default:
      return 'Offline';
  }
}

/**
 * Displays the current prioritized Smart-X operational issues.
 *
 * The component loads its authoritative state over HTTP and uses SignalR only
 * as an invalidation mechanism. This preserves a reliable fallback path while
 * still allowing changes to appear immediately during normal operation.
 */
function AdaptiveAnomalySpotlight({
  sensor,
  onSelectSensor,
  onSensorUpdated,
  onRealtimeChange,
}: AdaptiveAnomalySpotlightProps) {
  const [snapshot, setSnapshot] = useState<DashboardSnapshotResponse | null>(null);

  const [snapshotState, setSnapshotState] = useState<SnapshotState>('loading');

  const [snapshotError, setSnapshotError] = useState<string | null>(null);

  const [refreshError, setRefreshError] = useState<string | null>(null);

  const [acknowledgingIssueId, setAcknowledgingIssueId] = useState<string | null>(null);

  const [acknowledgementError, setAcknowledgementError] = useState<string | null>(null);

  const [showAll, setShowAll] = useState(false);

  /*
   * Identifies the newest outstanding HTTP snapshot request so a slower,
   * older response cannot overwrite newer dashboard state.
   */
  const requestSequenceRef = useRef(0);

  /*
   * Tracks whether at least one successful snapshot has already been loaded.
   * A temporary live-refresh failure can then preserve the current dashboard
   * rather than replacing useful issue data with a blank error view.
   */
  const hasSnapshotRef = useRef(false);

  /**
   * Retrieves the authoritative dashboard snapshot.
   */
  const loadSnapshot = useCallback(
    async (signal?: AbortSignal, showLoading = false): Promise<void> => {
      const requestSequence = ++requestSequenceRef.current;

      if (showLoading) {
        setSnapshotState('loading');
        setSnapshotError(null);
      }

      try {
        const response = await getDashboardSnapshot(false, signal);

        if (requestSequence !== requestSequenceRef.current) {
          return;
        }

        hasSnapshotRef.current = true;

        setSnapshot(response);
        setSnapshotState('ready');
        setSnapshotError(null);
        setRefreshError(null);
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
          return;
        }

        if (requestSequence !== requestSequenceRef.current) {
          return;
        }

        const message = getApiErrorMessage(
          error,
          'The Adaptive Anomaly Spotlight could not be loaded.',
        );

        if (showLoading || !hasSnapshotRef.current) {
          setSnapshotState('error');
          setSnapshotError(message);
          return;
        }

        /*
         * Preserve the last useful snapshot if a later live refresh fails.
         */
        setRefreshError(message);
      }
    },
    [],
  );

  /**
   * Reacts to SignalR invalidation events and reconnects.
   */
  const handleRealtimeChange = useCallback((): void => {
    void loadSnapshot(undefined, false);

    /*
     * Sensor status may also have changed, for example when the first
     * telemetry packet promotes an Offline sensor to Online.
     */
    onRealtimeChange();
  }, [loadSnapshot, onRealtimeChange]);

  const realtimeState = useDashboardRealtime(handleRealtimeChange);

  useEffect(() => {
    const controller = new AbortController();

    /*
     * Defer initial state-producing work until after the synchronous React
     * effect phase to remain compatible with the project's hooks lint rules.
     */
    const loadTimer = window.setTimeout(() => {
      void loadSnapshot(controller.signal, true);
    }, 0);

    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [loadSnapshot]);

  /**
   * Selects the affected sensor and moves keyboard/pointer focus directly to
   * the section containing the most useful diagnostic evidence.
   */
  function investigateIssue(issue: DashboardIssueResponse): void {
    onSelectSensor(issue.sensorProfileId);

    const targetSectionId = getTargetSectionId(issue.target);

    const nextUrl =
      `${window.location.pathname}` + `${window.location.search}` + `#${targetSectionId}`;

    window.history.replaceState(null, '', nextUrl);

    window.requestAnimationFrame(() => {
      const target = document.getElementById(targetSectionId);

      if (!target) {
        return;
      }

      const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

      target.scrollIntoView({
        behavior: reduceMotion ? 'auto' : 'smooth',
        block: 'start',
      });

      target.focus({
        preventScroll: true,
      });
    });
  }

  /**
   * Acknowledges one issue while preserving the underlying condition.
   */
  async function acknowledgeIssue(issue: DashboardIssueResponse): Promise<void> {
    setAcknowledgingIssueId(issue.id);

    setAcknowledgementError(null);

    try {
      await acknowledgeDashboardIssue(issue.id);

      /*
       * Refresh directly as well as relying on SignalR. This keeps the
       * initiating browser responsive even if the live transport is
       * temporarily reconnecting.
       */
      await loadSnapshot(undefined, false);
    } catch (error) {
      setAcknowledgementError(
        getApiErrorMessage(error, 'The dashboard issue could not be acknowledged.'),
      );
    } finally {
      setAcknowledgingIssueId(null);
    }
  }

  /**
   * Applies an immediately returned sensor-state update locally and refreshes
   * dashboard state without waiting for the corresponding SignalR event.
   */
  function handleSensorUpdated(updatedSensor: SensorResponse): void {
    onSensorUpdated(updatedSensor);

    onRealtimeChange();

    void loadSnapshot(undefined, false);
  }

  const issues = snapshot?.issues ?? [];

  const visibleIssues = showAll ? issues : issues.slice(0, defaultVisibleIssueCount);

  const hiddenIssueCount = Math.max(0, issues.length - defaultVisibleIssueCount);

  return (
    <section
      id="adaptive-anomaly-spotlight"
      className="adaptive-spotlight"
      aria-labelledby="adaptive-spotlight-title"
      tabIndex={-1}
    >
      <header className="spotlight-heading">
        <div>
          <p className="spotlight-label">ADAPTIVE ANOMALY SPOTLIGHT</p>

          <h2 id="adaptive-spotlight-title">Operational focus</h2>
        </div>

        <div className="spotlight-live-state">
          <span
            className={`spotlight-live-state-value spotlight-live-state-value--${realtimeState}`}
            aria-live="polite"
          >
            {formatRealtimeState(realtimeState)}
          </span>

          <button
            className="spotlight-refresh-action"
            type="button"
            onClick={() => {
              void loadSnapshot(undefined, !hasSnapshotRef.current);
            }}
          >
            Refresh
          </button>
        </div>
      </header>

      {snapshot && snapshotState === 'ready' && (
        <div className="spotlight-summary">
          <span>{snapshot.activeIssueCount} active</span>

          <span>{snapshot.unacknowledgedIssueCount} unacknowledged</span>
        </div>
      )}

      {sensor && (
        <SensorStateSimulator
          key={`${sensor.id}-${sensor.status}`}
          sensor={sensor}
          onSensorUpdated={handleSensorUpdated}
        />
      )}

      {snapshotState === 'loading' && (
        <p className="spotlight-empty-state" role="status">
          Loading operational state...
        </p>
      )}

      {snapshotState === 'error' && (
        <div className="spotlight-error-state" role="alert">
          <p>{snapshotError ?? 'The Adaptive Anomaly Spotlight could not be loaded.'}</p>

          <button
            className="spotlight-secondary-action"
            type="button"
            onClick={() => {
              void loadSnapshot(undefined, true);
            }}
          >
            Try again
          </button>
        </div>
      )}

      {snapshotState === 'ready' && issues.length === 0 && (
        <p className="spotlight-empty-state">No active issues.</p>
      )}

      {snapshotState === 'ready' && issues.length > 0 && (
        <>
          <ol className="spotlight-issue-list">
            {visibleIssues.map((issue) => {
              const severityClass = issue.severity.toLowerCase();

              const isAcknowledging = acknowledgingIssueId === issue.id;

              return (
                <li key={issue.id}>
                  <article
                    className={
                      issue.state === 'Acknowledged'
                        ? `spotlight-issue spotlight-issue--${severityClass} spotlight-issue--acknowledged`
                        : `spotlight-issue spotlight-issue--${severityClass}`
                    }
                  >
                    <div className="spotlight-issue-header">
                      <div className="spotlight-issue-classification">
                        <span className={`spotlight-severity spotlight-severity--${severityClass}`}>
                          {issue.severity}
                        </span>

                        <span>{formatIssueType(issue)}</span>
                      </div>

                      <span className="spotlight-priority">
                        Priority {Math.round(issue.priorityScore)}
                      </span>
                    </div>

                    <h3>{issue.title}</h3>

                    <p className="spotlight-issue-sensor">{issue.sensorName}</p>

                    <p className="spotlight-issue-detail">{issue.detail}</p>

                    <div className="spotlight-issue-meta">
                      <span>
                        Last observed{' '}
                        <time dateTime={issue.lastObservedAtUtc}>
                          {formatTimestamp(issue.lastObservedAtUtc)}
                        </time>
                      </span>

                      {issue.occurrenceCount > 1 && (
                        <span>{issue.occurrenceCount} occurrences</span>
                      )}

                      {issue.state === 'Acknowledged' && <span>Acknowledged</span>}
                    </div>

                    <div className="spotlight-issue-actions">
                      <button
                        className="spotlight-primary-action"
                        type="button"
                        onClick={() => {
                          investigateIssue(issue);
                        }}
                      >
                        Investigate
                      </button>

                      {issue.state === 'Active' && (
                        <button
                          className="spotlight-secondary-action"
                          type="button"
                          disabled={isAcknowledging}
                          onClick={() => {
                            void acknowledgeIssue(issue);
                          }}
                        >
                          {isAcknowledging ? 'Acknowledging...' : 'Acknowledge'}
                        </button>
                      )}
                    </div>
                  </article>
                </li>
              );
            })}
          </ol>

          {hiddenIssueCount > 0 && (
            <button
              className="spotlight-show-all"
              type="button"
              onClick={() => {
                setShowAll((current) => !current);
              }}
            >
              {showAll ? 'Show priority view' : `Show all ${issues.length}`}
            </button>
          )}
        </>
      )}

      {refreshError && (
        <p className="spotlight-refresh-error" role="alert">
          Live refresh failed. Current snapshot retained.
        </p>
      )}

      {acknowledgementError && (
        <p className="spotlight-refresh-error" role="alert">
          {acknowledgementError}
        </p>
      )}
    </section>
  );
}

export default AdaptiveAnomalySpotlight;
