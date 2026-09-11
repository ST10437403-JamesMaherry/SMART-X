import type { SensorResponse } from '../../api/sensorsApi';

interface SensorRegistryProps {
  readonly sensors: readonly SensorResponse[];
  readonly selectedSensorId: string | null;
  readonly state: 'loading' | 'ready' | 'error';
  readonly errorMessage: string | null;
  readonly onSelect: (sensorId: string) => void;
  readonly onRetry: () => void;
}

/**
 * Formats transport enum values for concise operator-facing display.
 */
function formatCategory(category: SensorResponse['category']): string {
  return category === 'PowerConsumption' ? 'Power consumption' : category;
}

/**
 * Displays registered Smart-X sensors and maintains an explicit selected
 * sensor state for the telemetry and attachment workflows implemented next.
 */
function SensorRegistry({
  sensors,
  selectedSensorId,
  state,
  errorMessage,
  onSelect,
  onRetry,
}: SensorRegistryProps) {
  return (
    <section className="sensor-panel sensor-registry" aria-labelledby="registered-sensors-title">
      <div className="sensor-panel-heading">
        <h3 id="registered-sensors-title">Registered sensors</h3>

        <span className="sensor-count" aria-label={`${sensors.length} registered sensors`}>
          {String(sensors.length).padStart(2, '0')}
        </span>
      </div>

      {state === 'loading' && (
        <p className="sensor-registry-state" role="status">
          Loading sensors...
        </p>
      )}

      {state === 'error' && (
        <div className="sensor-registry-state" role="alert">
          <p>{errorMessage ?? 'Sensors could not be loaded.'}</p>

          <button className="sensor-secondary-action" type="button" onClick={onRetry}>
            Try again
          </button>
        </div>
      )}

      {state === 'ready' && sensors.length === 0 && (
        <p className="sensor-registry-state">No sensors registered.</p>
      )}

      {state === 'ready' && sensors.length > 0 && (
        <ul className="sensor-registry-list">
          {sensors.map((sensor) => {
            const isSelected = sensor.id === selectedSensorId;

            return (
              <li key={sensor.id}>
                <button
                  className={isSelected ? 'sensor-row sensor-row--selected' : 'sensor-row'}
                  type="button"
                  aria-pressed={isSelected}
                  onClick={() => {
                    onSelect(sensor.id);
                  }}
                >
                  <span className="sensor-row-primary">
                    <strong>{sensor.name}</strong>

                    <span>{sensor.deviceIdentifier}</span>
                  </span>

                  <span className="sensor-row-meta">
                    <span>{formatCategory(sensor.category)}</span>
                    <span>{sensor.telemetryDataType}</span>
                    <span>{sensor.status}</span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

export default SensorRegistry;
