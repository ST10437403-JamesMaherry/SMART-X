import { useEffect, useState, type FormEvent } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import type { TelemetryDataType } from '../../api/sensorsApi';
import type { TelemetryReadingResponse } from '../../api/telemetryApi';
import {
  getIndexedTelemetryPage,
  localRangeToUtc,
  TOPOLOGY_HISTORY_PAGE_SIZES,
  type TelemetryRangeCursor,
  type TelemetryRangePageResponse,
  type TelemetryRangeQuery,
} from '../../api/telemetryRangeApi';
import './TopologySensorHistory.css';

interface TopologySensorHistoryProps {
  readonly sensorProfileId: string | null;
  readonly sensorName: string | null;
  readonly deviceIdentifier: string | null;
  readonly telemetryDataType: TelemetryDataType | null;
}

type HistoryLoadState =
  | { readonly phase: 'idle' | 'loading'; readonly page: null; readonly error: null }
  | { readonly phase: 'ready'; readonly page: TelemetryRangePageResponse; readonly error: null }
  | { readonly phase: 'error'; readonly page: null; readonly error: string };

/** Produces an HTML datetime-local value from a Date, preserving local time. */
function toLocalInputValue(date: Date): string {
  const offsetMillis = date.getTimezoneOffset() * 60_000;
  return new Date(date.getTime() - offsetMillis).toISOString().slice(0, 16);
}

/**
 * Present the exact numeric value without rounding or interpreting false as
 * missing. Typed null payloads never appear as fabricated zeroes.
 */
function renderTypedValue(reading: TelemetryReadingResponse): string {
  switch (reading.telemetryDataType) {
    case 'Float':
      return reading.floatValue === null ? 'Unavailable' : String(reading.floatValue);
    case 'Integer':
      return reading.integerValue === null ? 'Unavailable' : String(reading.integerValue);
    case 'Boolean':
      return reading.booleanValue === null
        ? 'Unavailable'
        : reading.booleanValue
          ? 'True'
          : 'False';
  }
}

/** Display UTC explicitly so local-time form input cannot cause ambiguity. */
function renderUtcTimestamp(timestamp: string): string {
  const date = new Date(timestamp);
  return Number.isNaN(date.getTime())
    ? timestamp
    : date.toISOString().replace('T', ' ').replace('Z', ' UTC');
}

/**
 * P3-09: history is fetched independently of topology projections. This
 * component stays mounted when topology refreshes, while the key assigned by
 * TopologyPage resets only on a deliberate change of selected sensor.
 *
 * Only one AVL page is retained in React state; previous pages are revisited
 * by remembering small composite cursors rather than caching whole datasets.
 */
export function TopologySensorHistory({
  sensorProfileId,
  sensorName,
  deviceIdentifier,
  telemetryDataType,
}: TopologySensorHistoryProps) {
  const [fromLocal, setFromLocal] = useState(() =>
    toLocalInputValue(new Date(Date.now() - 24 * 60 * 60 * 1000)),
  );
  const [toLocal, setToLocal] = useState(() => toLocalInputValue(new Date()));
  const [pageSize, setPageSize] = useState<number>(25);
  const [query, setQuery] = useState<TelemetryRangeQuery | null>(null);
  const [cursors, setCursors] = useState<readonly (TelemetryRangeCursor | null)[]>([null]);
  const [load, setLoad] = useState<HistoryLoadState>({
    phase: 'idle',
    page: null,
    error: null,
  });

  // A request only occurs after an operator submits valid time bounds, or
  // moves to a page with the same accepted query. The AbortController prevents
  // old requests from overwriting newer results or a newly selected sensor.
  useEffect(() => {
    if (query === null || sensorProfileId === null) return;

    const controller = new AbortController();
    const cursor = cursors[cursors.length - 1] ?? null;

    void getIndexedTelemetryPage(sensorProfileId, query, cursor, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        if (page.sensorProfileId.toLowerCase() !== sensorProfileId.toLowerCase()) {
          throw new Error('The telemetry API returned a page for another sensor.');
        }
        if (page.hasMore && page.nextCursor === null) {
          throw new Error('The telemetry API did not return a required continuation cursor.');
        }
        setLoad({ phase: 'ready', page, error: null });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({
          phase: 'error',
          page: null,
          error: getApiErrorMessage(error, 'Indexed telemetry could not be loaded. Try again.'),
        });
      });

    return () => controller.abort();
  }, [query, cursors, sensorProfileId]);

  // Editing the draft makes any prior result semantically stale. Cancel the
  // old query before its response can be presented under newly edited bounds.
  function invalidateResults(): void {
    setQuery(null);
    setCursors([null]);
    setLoad({ phase: 'idle', page: null, error: null });
  }

  function onQuery(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault();
    if (sensorProfileId === null) return;

    try {
      const bounds = localRangeToUtc(fromLocal, toLocal);
      setCursors([null]);
      setLoad({ phase: 'loading', page: null, error: null });
      setQuery({ ...bounds, limit: pageSize });
    } catch (error: unknown) {
      setQuery(null);
      setLoad({
        phase: 'error',
        page: null,
        error: error instanceof Error ? error.message : 'Select a valid time range.',
      });
    }
  }

  function nextPage(): void {
    if (load.phase !== 'ready' || !load.page.hasMore || load.page.nextCursor === null) return;
    const nextCursor = load.page.nextCursor;
    setLoad({ phase: 'loading', page: null, error: null });
    setCursors((previous) => [...previous, nextCursor]);
  }

  function previousPage(): void {
    if (cursors.length <= 1) return;
    setLoad({ phase: 'loading', page: null, error: null });
    setCursors((previous) => previous.slice(0, -1));
  }

  const pageNumber = cursors.length;
  const isBusy = load.phase === 'loading';
  const hasSelection = sensorProfileId !== null;

  return (
    <section className="topology-history" aria-labelledby="topology-history-heading">
      <div className="topology-history__heading-row">
        <div>
          <p className="topology-history__eyebrow">TELEMETRY / AVL INDEX</p>
          <h2 id="topology-history-heading">Device history</h2>
          <p className="topology-history__intro">
            Query accepted readings by capture time using the server's AVL index. The inclusive
            range is evaluated in UTC; no recent-history approximation is used.
          </p>
        </div>
        <span className="topology-history__index-label">INDEX / TIMESTAMP</span>
      </div>

      {!hasSelection ? (
        <p className="topology-history__notice" role="status">
          Select a registered sensor above or in the mesh to inspect its chronological telemetry.
        </p>
      ) : (
        <>
          <dl className="topology-history__identity" aria-label="History sensor identity">
            <div>
              <dt>Selected device</dt>
              <dd>{sensorName ?? 'Loading registration'}</dd>
            </div>
            <div>
              <dt>Identifier / MAC</dt>
              <dd>{deviceIdentifier ?? 'Loading registration'}</dd>
            </div>
            <div>
              <dt>Sensor UUID</dt>
              <dd>{sensorProfileId}</dd>
            </div>
            <div>
              <dt>Reading type</dt>
              <dd>{telemetryDataType ?? 'Loading registration'}</dd>
            </div>
          </dl>

          <form className="topology-history__filters" onSubmit={onQuery}>
            <label>
              From (local time)
              <input
                type="datetime-local"
                required
                value={fromLocal}
                onChange={(event) => {
                  setFromLocal(event.target.value);
                  invalidateResults();
                }}
              />
            </label>
            <label>
              To (local time)
              <input
                type="datetime-local"
                required
                value={toLocal}
                onChange={(event) => {
                  setToLocal(event.target.value);
                  invalidateResults();
                }}
              />
            </label>
            <label>
              Readings per page
              <select
                value={pageSize}
                onChange={(event) => {
                  setPageSize(Number(event.target.value));
                  invalidateResults();
                }}
              >
                {TOPOLOGY_HISTORY_PAGE_SIZES.map((size) => (
                  <option key={size} value={size}>
                    {size}
                  </option>
                ))}
              </select>
            </label>
            <button type="submit" disabled={isBusy}>
              Query AVL history
            </button>
          </form>

          {load.phase === 'idle' && (
            <p className="topology-history__notice" role="status">
              Choose a time range and query the selected sensor. Defaults to the past 24 hours.
            </p>
          )}
          {load.phase === 'loading' && (
            <p className="topology-history__notice" role="status">
              Loading one chronological AVL page…
            </p>
          )}
          {load.phase === 'error' && (
            <p className="topology-history__notice topology-history__notice--error" role="alert">
              {load.error}
            </p>
          )}
          {load.phase === 'ready' && (
            <>
              <div className="topology-history__page-heading" role="status">
                <span>
                  Page {pageNumber} · {load.page.readings.length} reading
                  {load.page.readings.length === 1 ? '' : 's'} on this page
                </span>
                <span>
                  {load.page.hasMore ? 'Additional indexed readings available' : 'End of range'}
                </span>
              </div>
              {load.page.readings.length === 0 ? (
                <p className="topology-history__notice">
                  No accepted telemetry was captured in the requested time range for this sensor.
                  Try wider bounds, or ingest readings in the Ingestion pillar.
                </p>
              ) : (
                <div
                  className="topology-history__table-scroll"
                  role="region"
                  aria-label="Scrollable AVL history table"
                  tabIndex={0}
                >
                  <table className="topology-history__table">
                    <caption>
                      Timestamp-ordered AVL history for {sensorName ?? sensorProfileId}, page{' '}
                      {pageNumber}
                    </caption>
                    <thead>
                      <tr>
                        <th scope="col">Reading</th>
                        <th scope="col">Typed value</th>
                        <th scope="col">Data type</th>
                        <th scope="col">Captured (UTC)</th>
                      </tr>
                    </thead>
                    <tbody>
                      {load.page.readings.map((reading, index) => (
                        // Index is a page-local row position only. The API
                        // does not expose a durable per-packet ID.
                        <tr key={`${reading.capturedAtUtc}-${index}`}>
                          <td>#{String(index + 1).padStart(2, '0')}</td>
                          <td className="topology-history__value">{renderTypedValue(reading)}</td>
                          <td>{reading.telemetryDataType}</td>
                          <td>
                            <time dateTime={reading.capturedAtUtc}>
                              {renderUtcTimestamp(reading.capturedAtUtc)}
                            </time>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="topology-history__pagination" aria-label="Indexed history pagination">
                <button type="button" disabled={pageNumber <= 1} onClick={previousPage}>
                  Previous page
                </button>
                <button type="button" disabled={!load.page.hasMore} onClick={nextPage}>
                  Next page
                </button>
              </div>
              <p className="topology-history__footnote">
                Same-timestamp readings retain server acceptance order. Pagination follows the
                server's timestamp + sequence cursor; totals and cross-request snapshot isolation
                are not claimed. Stored telemetry is process-local.
              </p>
            </>
          )}
        </>
      )}
    </section>
  );
}
