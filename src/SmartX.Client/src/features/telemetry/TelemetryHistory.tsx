import type { SensorResponse, TelemetryDataType } from '../../api/sensorsApi';
import type { TelemetryReadingResponse } from '../../api/telemetryApi';

interface TelemetryHistoryProps {
  readonly sensor: SensorResponse;
  readonly readings: readonly TelemetryReadingResponse[];
  readonly state: 'loading' | 'ready' | 'error';
  readonly errorMessage: string | null;
  readonly onRetry: () => void;
}

/**
 * Extracts the strongly typed payload from a telemetry API response.
 */
function getTelemetryValue(reading: TelemetryReadingResponse): string {
  if (reading.telemetryDataType === 'Float') {
    return reading.floatValue?.toString() ?? '—';
  }

  if (reading.telemetryDataType === 'Integer') {
    return reading.integerValue?.toString() ?? '—';
  }

  if (reading.booleanValue === null) {
    return '—';
  }

  return reading.booleanValue ? 'True' : 'False';
}

/**
 * Converts transport type names into concise UI labels.
 */
function formatTelemetryType(telemetryType: TelemetryDataType): string {
  return telemetryType === 'Float' ? 'Decimal' : telemetryType;
}

/**
 * Formats a UTC API timestamp while preserving an accessible full timestamp
 * through the native time element.
 */
function formatTelemetryTimestamp(timestamp: string): string {
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
 * Displays recent telemetry in the same order returned by the backend.
 *
 * Smart-X does not currently expose a permanent server-side sequence number.
 * The displayed sequence therefore identifies the packet's position within
 * the current ordered recent-history result rather than inventing persistent
 * packet identity.
 */
function TelemetryHistory({
  sensor,
  readings,
  state,
  errorMessage,
  onRetry,
}: TelemetryHistoryProps) {
  return (
    <section
      className="telemetry-panel telemetry-history"
      aria-labelledby="telemetry-history-title"
    >
      <header className="telemetry-panel-heading">
        <h3 id="telemetry-history-title">Recent telemetry</h3>

        <span
          className="telemetry-count"
          aria-label={`${readings.length} recent telemetry readings`}
        >
          {String(readings.length).padStart(2, '0')}
        </span>
      </header>

      {state === 'loading' && (
        <p className="telemetry-empty-state" role="status">
          Loading telemetry...
        </p>
      )}

      {state === 'error' && (
        <div className="telemetry-empty-state" role="alert">
          <p>{errorMessage ?? 'Recent telemetry could not be loaded.'}</p>

          <button className="telemetry-secondary-action" type="button" onClick={onRetry}>
            Try again
          </button>
        </div>
      )}

      {state === 'ready' && readings.length === 0 && (
        <p className="telemetry-empty-state">No telemetry recorded for {sensor.name}.</p>
      )}

      {state === 'ready' && readings.length > 0 && (
        <div className="telemetry-table-wrapper">
          <table className="telemetry-table">
            <caption className="visually-hidden">Recent telemetry for {sensor.name}</caption>

            <thead>
              <tr>
                <th scope="col">Sequence</th>
                <th scope="col">Value</th>
                <th scope="col">Type</th>
                <th scope="col">Captured</th>
              </tr>
            </thead>

            <tbody>
              {readings.map((reading, index) => {
                /*
                 * The API does not expose a persistent packet ID, so the
                 * timestamp and ordered position form a stable React key for
                 * the current response snapshot.
                 */
                const rowKey = `${reading.capturedAtUtc}-` + `${reading.deviceIdentifier}-${index}`;

                return (
                  <tr key={rowKey}>
                    <td>#{String(index + 1).padStart(3, '0')}</td>

                    <td className="telemetry-value">{getTelemetryValue(reading)}</td>

                    <td>{formatTelemetryType(reading.telemetryDataType)}</td>

                    <td>
                      <time dateTime={reading.capturedAtUtc}>
                        {formatTelemetryTimestamp(reading.capturedAtUtc)}
                      </time>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export default TelemetryHistory;
