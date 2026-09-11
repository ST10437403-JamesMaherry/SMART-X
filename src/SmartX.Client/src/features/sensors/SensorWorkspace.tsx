import { useCallback, useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { getSensors, type SensorResponse } from '../../api/sensorsApi';
import SensorRegistrationForm from './SensorRegistrationForm';
import SensorRegistry from './SensorRegistry';
import './SensorWorkspace.css';

/**
 * Owns the sensor collection and selected-sensor state for the complete Part 1
 * ingestion workspace.
 *
 * Telemetry and attachment functionality will be integrated at this level in
 * the next commits so that every feature works against the same sensor
 * selection.
 */
function SensorWorkspace() {
  const [sensors, setSensors] = useState<readonly SensorResponse[]>([]);

  const [selectedSensorId, setSelectedSensorId] = useState<string | null>(null);

  const [registryState, setRegistryState] = useState<'loading' | 'ready' | 'error'>('loading');

  const [registryError, setRegistryError] = useState<string | null>(null);

  /**
   * Retrieves the current sensor registry from the Smart-X API.
   */
  const loadSensors = useCallback(async (signal?: AbortSignal): Promise<void> => {
    setRegistryState('loading');
    setRegistryError(null);

    try {
      const response = await getSensors(signal);

      setSensors(response);

      /*
       * Preserve the current sensor when possible. Otherwise select the
       * first available sensor so the later telemetry/attachment workflows
       * have a sensible initial context.
       */
      setSelectedSensorId((currentSensorId) => {
        if (currentSensorId && response.some((sensor) => sensor.id === currentSensorId)) {
          return currentSensorId;
        }

        return response[0]?.id ?? null;
      });

      setRegistryState('ready');
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        return;
      }

      setRegistryState('error');

      setRegistryError(
        getApiErrorMessage(error, 'The Smart-X sensor registry could not be loaded.'),
      );
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();

    /*
     * Defer the asynchronous load beyond the synchronous effect phase. This
     * follows the same React hooks lint-safe pattern as the API health check.
     */
    const loadTimer = window.setTimeout(() => {
      void loadSensors(controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [loadSensors]);

  /**
   * Adds a newly registered sensor directly to the current registry and makes
   * it the selected sensor.
   */
  function handleRegistered(sensor: SensorResponse): void {
    setSensors((currentSensors) => [
      sensor,
      ...currentSensors.filter((currentSensor) => currentSensor.id !== sensor.id),
    ]);

    setSelectedSensorId(sensor.id);
    setRegistryError(null);
    setRegistryState('ready');
  }

  return (
    <section className="sensor-workspace" aria-labelledby="sensor-workspace-title">
      <header className="sensor-workspace-heading">
        <p className="sensor-workspace-label">SENSOR MANAGEMENT</p>

        <h2 id="sensor-workspace-title">Sensor registry</h2>
      </header>

      <div className="sensor-workspace-grid">
        <SensorRegistrationForm onRegistered={handleRegistered} />

        <SensorRegistry
          sensors={sensors}
          selectedSensorId={selectedSensorId}
          state={registryState}
          errorMessage={registryError}
          onSelect={setSelectedSensorId}
          onRetry={() => {
            void loadSensors();
          }}
        />
      </div>
    </section>
  );
}

export default SensorWorkspace;
