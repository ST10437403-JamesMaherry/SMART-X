import { useCallback, useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { getSensors, type SensorResponse } from '../../api/sensorsApi';
import IngestionSectionNavigation from '../../components/IngestionSectionNavigation';
import AttachmentWorkspace from '../attachments/AttachmentWorkspace';
import AdaptiveAnomalySpotlight from '../dashboard/AdaptiveAnomalySpotlight';
import TelemetryWorkspace from '../telemetry/TelemetryWorkspace';
import SensorRegistrationForm from './SensorRegistrationForm';
import SensorRegistry from './SensorRegistry';
import './SensorWorkspace.css';

/**
 * Owns the shared sensor context for the complete Part 1 ingestion module.
 *
 * Sensor registration, telemetry, attachments and the Adaptive Anomaly
 * Spotlight deliberately operate against one selected sensor.
 */
function SensorWorkspace() {
  const [sensors, setSensors] = useState<readonly SensorResponse[]>([]);

  const [selectedSensorId, setSelectedSensorId] = useState<string | null>(null);

  const [registryState, setRegistryState] = useState<'loading' | 'ready' | 'error'>('loading');

  const [registryError, setRegistryError] = useState<string | null>(null);

  /**
   * Applies a newly retrieved sensor collection while preserving the current
   * sensor selection whenever that sensor still exists.
   */
  const applySensorCollection = useCallback((response: readonly SensorResponse[]): void => {
    setSensors(response);

    setSelectedSensorId((currentSensorId) => {
      if (currentSensorId && response.some((sensor) => sensor.id === currentSensorId)) {
        return currentSensorId;
      }

      return response[0]?.id ?? null;
    });
  }, []);

  /**
   * Performs the primary sensor-registry load with visible loading and failure
   * states.
   */
  const loadSensors = useCallback(
    async (signal?: AbortSignal): Promise<void> => {
      setRegistryState('loading');
      setRegistryError(null);

      try {
        const response = await getSensors(signal);

        applySensorCollection(response);

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
    },
    [applySensorCollection],
  );

  /**
   * Performs a quiet background refresh after SignalR reports broader system
   * changes.
   *
   * Existing sensor data remains on screen if this background request fails;
   * the Adaptive Anomaly Spotlight separately communicates live connectivity.
   */
  const refreshSensors = useCallback(async (): Promise<void> => {
    try {
      const response = await getSensors();

      applySensorCollection(response);
    } catch {
      /*
       * Preserve the current usable registry during a temporary background
       * refresh failure. Primary registry loading already has a dedicated
       * visible error state.
       */
    }
  }, [applySensorCollection]);

  useEffect(() => {
    const controller = new AbortController();

    /*
     * Delay the asynchronous state-producing operation beyond React's
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
   * makes it the selected device.
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
   * Applies a server-confirmed update to one existing sensor.
   *
   * This is used by the Part 1 state simulator before the asynchronous
   * background registry refresh arrives.
   */
  function handleSensorUpdated(sensor: SensorResponse): void {
    setSensors((currentSensors) => {
      const exists = currentSensors.some((currentSensor) => currentSensor.id === sensor.id);

      if (!exists) {
        return [sensor, ...currentSensors];
      }

      return currentSensors.map((currentSensor) =>
        currentSensor.id === sensor.id ? sensor : currentSensor,
      );
    });

    setSelectedSensorId(sensor.id);
  }

  /**
   * Refreshes sensor status in response to SignalR dashboard invalidations.
   *
   * This is particularly important when an Offline sensor automatically
   * becomes Online after its first valid telemetry packet.
   */
  const handleRealtimeChange = useCallback((): void => {
    void refreshSensors();
  }, [refreshSensors]);

  /**
   * Resolve the complete selected sensor once so all downstream features
   * consume the same server-backed profile.
   */
  const selectedSensor = sensors.find((sensor) => sensor.id === selectedSensorId) ?? null;

  const isRegistryReady = registryState === 'ready';

  return (
    <>
      {isRegistryReady && <IngestionSectionNavigation />}

      {isRegistryReady && (
        <AdaptiveAnomalySpotlight
          sensor={selectedSensor}
          onSelectSensor={setSelectedSensorId}
          onSensorUpdated={handleSensorUpdated}
          onRealtimeChange={handleRealtimeChange}
        />
      )}

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

      {isRegistryReady && (
        <>
          <TelemetryWorkspace sensor={selectedSensor} />

          {/*
           * Remount attachment state whenever the physical device context
           * changes so a pending file can never carry from one sensor into
           * another.
           */}
          <AttachmentWorkspace key={selectedSensor?.id ?? 'no-sensor'} sensor={selectedSensor} />
        </>
      )}

      <p className="visually-hidden" aria-live="polite" aria-atomic="true">
        {selectedSensor ? `Selected sensor: ${selectedSensor.name}.` : 'No sensor selected.'}
      </p>
    </>
  );
}

export default SensorWorkspace;
