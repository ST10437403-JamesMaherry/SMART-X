import { useCallback, useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { getSensors, type SensorResponse } from '../../api/sensorsApi';
import IngestionSectionNavigation from '../../components/IngestionSectionNavigation';
import AttachmentWorkspace from '../attachments/AttachmentWorkspace';
import TelemetryWorkspace from '../telemetry/TelemetryWorkspace';
import SensorRegistrationForm from './SensorRegistrationForm';
import SensorRegistry from './SensorRegistry';
import './SensorWorkspace.css';

/**
 * Owns the shared sensor state for the complete Part 1 ingestion module.
 *
 * Sensor registration, telemetry and attachments intentionally depend on one
 * selected sensor. This avoids duplicating sensor selectors across separate
 * feature areas and keeps device context consistent throughout the interface.
 */
function SensorWorkspace() {
  const [sensors, setSensors] = useState<readonly SensorResponse[]>([]);

  const [selectedSensorId, setSelectedSensorId] = useState<string | null>(null);

  const [registryState, setRegistryState] = useState<'loading' | 'ready' | 'error'>('loading');

  const [registryError, setRegistryError] = useState<string | null>(null);

  /**
   * Retrieves the current Smart-X sensor registry.
   */
  const loadSensors = useCallback(async (signal?: AbortSignal): Promise<void> => {
    setRegistryState('loading');
    setRegistryError(null);

    try {
      const response = await getSensors(signal);

      setSensors(response);

      /*
       * Preserve an existing selection when the sensor still exists.
       *
       * Otherwise select the first available sensor so telemetry and
       * attachment features receive a useful initial context.
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

      setSensors([]);
      setSelectedSensorId(null);
      setRegistryState('error');

      setRegistryError(
        getApiErrorMessage(error, 'The Smart-X sensor registry could not be loaded.'),
      );
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();

    /*
     * Delay the initial asynchronous state-producing operation beyond React's
     * synchronous effect phase to satisfy the project's hooks lint policy.
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
   * Inserts a newly registered sensor directly into the current registry and
   * makes it the active device context.
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

  /**
   * Resolve the complete selected sensor once so every downstream feature
   * consumes the same profile.
   */
  const selectedSensor = sensors.find((sensor) => sensor.id === selectedSensorId) ?? null;

  const isRegistryReady = registryState === 'ready';

  return (
    <>
      {/*
       * Section navigation is only displayed once the registry is available.
       * This avoids exposing links to workflows that cannot operate while the
       * API or sensor registry is unavailable.
       */}
      {isRegistryReady && <IngestionSectionNavigation />}

      <section
        id="sensor-management"
        className="sensor-workspace"
        aria-labelledby="sensor-workspace-title"
        tabIndex={-1}
      >
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

      {/*
       * Telemetry and attachment features depend on the registry. They are
       * withheld while registry loading fails rather than displaying several
       * misleading "no sensor" states at the same time.
       */}
      {isRegistryReady && (
        <>
          <TelemetryWorkspace sensor={selectedSensor} />

          {/*
           * Changing sensors remounts attachment state so a file selected for
           * one physical device cannot accidentally carry into another.
           */}
          <AttachmentWorkspace key={selectedSensor?.id ?? 'no-sensor'} sensor={selectedSensor} />
        </>
      )}

      {/*
       * Announce device-context changes without adding visible interface
       * clutter. This is especially useful when the sensor registry is
       * operated entirely from the keyboard.
       */}
      <p className="visually-hidden" aria-live="polite" aria-atomic="true">
        {selectedSensor ? `Selected sensor: ${selectedSensor.name}.` : 'No sensor selected.'}
      </p>
    </>
  );
}

export default SensorWorkspace;
