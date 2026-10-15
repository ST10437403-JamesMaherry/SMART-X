import { useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  getRealtimeHistory,
  type RealtimeHistoryEventResponse,
  type RealtimeHistoryResponse,
} from '../../api/realtimeApi';

interface RealtimeHistoryPanelProps {
  readonly deviceId: string;
  readonly refreshToken: number;
}

const visibleHistoryLimit = 40;

/** Render event payloads without conflating reported telemetry and commands. */
function describeEvent(entry: RealtimeHistoryEventResponse): string {
  if (entry.telemetry) {
    const telemetry = entry.telemetry;
    switch (telemetry.telemetryDataType) {
      case 'Float':
        return `Float ${telemetry.floatValue ?? 'unavailable'}`;
      case 'Integer':
        return `Integer ${telemetry.integerValue ?? 'unavailable'}`;
      case 'Boolean':
        return `Boolean ${telemetry.booleanValue === null ? 'unavailable' : telemetry.booleanValue}`;
    }
  }

  if (entry.command) {
    return `${entry.command.previousState} → ${entry.command.resultingState}`;
  }

  return 'Payload unavailable';
}

function describeKind(entry: RealtimeHistoryEventResponse): string {
  if (entry.kind === 'TelemetryProcessed') return 'Telemetry processed';
  if (entry.kind === 'RelayUndo') return 'Undo reversal';
  if (entry.command && !entry.command.stateChanged) return 'Accepted no-op';
  return 'Relay command';
}

/** Occurrence time, not HTTP acceptance or dispatch completion time. */
function formatUtc(timestamp: string): string {
  const date = new Date(timestamp);
  return Number.isNaN(date.getTime())
    ? timestamp
    : `${date.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

/**
 * Display actual P2-05 SortedDictionary entries in reverse chronological
 * order. The server applies sorting and the 40-row limit; the browser does
 * not infer a timeline from ingestion order or recent dispatch diagnostics.
 *
 * Refreshes cancel outdated requests, preserve last-known data on temporary
 * network failure, and reset entirely when the parent changes device keys.
 */
export default function RealtimeHistoryPanel({
  deviceId,
  refreshToken,
}: RealtimeHistoryPanelProps) {
  const [history, setHistory] = useState<RealtimeHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [retrySequence, setRetrySequence] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      async function load(): Promise<void> {
        setLoading(true);
        setError(null);
        try {
          const response = await getRealtimeHistory(
            deviceId,
            visibleHistoryLimit,
            'desc',
            controller.signal,
          );
          if (!controller.signal.aborted) setHistory(response);
        } catch (reason) {
          if (!controller.signal.aborted) {
            setError(getApiErrorMessage(reason, 'Unable to retrieve chronological history.'));
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
  }, [deviceId, refreshToken, retrySequence]);

  return (
    <section className="realtime-section" aria-labelledby="realtime-history-title">
      <header className="realtime-section-header">
        <p className="realtime-kicker">05 / SORTED OPERATIONAL HISTORY</p>
        <h3 id="realtime-history-title">Chronological history</h3>
        <p className="realtime-section-detail">
          Latest {visibleHistoryLimit} retained events · occurrence time · newest first
          {history ? ` · ${history.totalRecordedEvents.toLocaleString('en-ZA')} recorded` : ''}
        </p>
      </header>

      {loading && (
        <p className="realtime-explainer" role="status">
          Refreshing device history...
        </p>
      )}

      {error && (
        <div className="realtime-inline-error" role="alert">
          <p>
            {error}
            {history ? ' Last successful history retained.' : ''}
          </p>
          <button
            type="button"
            className="realtime-action"
            onClick={() => setRetrySequence((current) => current + 1)}
          >
            Retry device history
          </button>
        </div>
      )}

      {!history && !loading && !error && <p className="realtime-empty">No history loaded.</p>}
      {history && history.events.length === 0 && (
        <p className="realtime-empty">
          No successful processing or accepted commands for this device.
        </p>
      )}
      {history && history.events.length > 0 && (
        <div className="realtime-table-scroll">
          <table className="realtime-table" aria-label="Chronological device history">
            <thead>
              <tr>
                <th scope="col">Occurred (UTC)</th>
                <th scope="col">Event</th>
                <th scope="col">Payload / change</th>
                <th scope="col">Lane</th>
                <th scope="col">Sequence</th>
              </tr>
            </thead>
            <tbody>
              {history.events.map((entry) => (
                <tr key={entry.eventId}>
                  <td>
                    <time dateTime={entry.occurredAtUtc}>{formatUtc(entry.occurredAtUtc)}</time>
                  </td>
                  <td>{describeKind(entry)}</td>
                  <td>
                    {describeEvent(entry)}
                    {entry.command?.isReversal && entry.command.reversesCommandId && (
                      <span className="realtime-history-context">
                        Reverses {entry.command.reversesCommandId.slice(0, 8)}
                      </span>
                    )}
                  </td>
                  <td>
                    {entry.processingLane ?? 'Command'}
                    {entry.severity ? ` / ${entry.severity}` : ''}
                  </td>
                  <td>{entry.sequence}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="realtime-explainer">
        Arrival, worker dispatch and capture-time order can differ. Equal timestamps keep distinct
        history events; accepted-but-unprocessed telemetry does not appear here.
      </p>
    </section>
  );
}
