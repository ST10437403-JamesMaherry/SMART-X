import { useCallback, useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import type { SensorResponse } from '../../api/sensorsApi';
import { getRecentTelemetry, type TelemetryReadingResponse } from '../../api/telemetryApi';
import TelemetryBatchSimulator from './TelemetryBatchSimulator';
import TelemetryHistory from './TelemetryHistory';
import TelemetrySimulator from './TelemetrySimulator';
import './TelemetryWorkspace.css';

interface TelemetryWorkspaceProps {
  readonly sensor: SensorResponse | null;
}

/**
 * Maximum number of recent readings shown by the Part 1 client.
 *
 * The backend accepts recent-history limits from 1 to 100 and defaults to 50.
 */
const recentTelemetryLimit = 50;

/**
 * Owns recent telemetry state for the currently selected Smart-X sensor.
 *
 * Individual readings and historical batches share this workspace so accepted
 * data immediately appears in one ordered history view.
 */
function TelemetryWorkspace({ sensor }: TelemetryWorkspaceProps) {
  const [readings, setReadings] = useState<readonly TelemetryReadingResponse[]>([]);

  const [historyState, setHistoryState] = useState<'loading' | 'ready' | 'error'>('ready');

  const [historyError, setHistoryError] = useState<string | null>(null);

  /**
   * Loads recent telemetry for one registered sensor.
   */
  const loadTelemetry = useCallback(
    async (sensorId: string, signal?: AbortSignal): Promise<void> => {
      setHistoryState('loading');
      setHistoryError(null);

      try {
        const response = await getRecentTelemetry(sensorId, recentTelemetryLimit, signal);

        setReadings(response);
        setHistoryState('ready');
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
          return;
        }

        setReadings([]);
        setHistoryState('error');

        setHistoryError(getApiErrorMessage(error, 'Recent telemetry could not be loaded.'));
      }
    },
    [],
  );

  useEffect(() => {
    if (!sensor) {
      return;
    }

    const controller = new AbortController();

    /*
     * Defer the asynchronous state-producing operation until after React's
     * synchronous effect phase to remain compatible with hooks lint rules.
     */
    const loadTimer = window.setTimeout(() => {
      void loadTelemetry(sensor.id, controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [loadTelemetry, sensor]);

  /**
   * Adds one API-confirmed reading directly to the current history without
   * requiring another network request.
   */
  function handleTelemetryAccepted(reading: TelemetryReadingResponse): void {
    setReadings((currentReadings) => {
      const updatedReadings = [...currentReadings, reading];

      return updatedReadings.slice(-recentTelemetryLimit);
    });

    setHistoryError(null);
    setHistoryState('ready');
  }

  /**
   * Batch ingestion returns a processing summary instead of each individual
   * reading, so the authoritative recent-history endpoint is refreshed after
   * a successful batch.
   */
  function handleBatchAccepted(): void {
    if (!sensor) {
      return;
    }

    void loadTelemetry(sensor.id);
  }

  if (!sensor) {
    return (
      <section className="telemetry-workspace">
        <header className="telemetry-workspace-heading">
          <p className="telemetry-workspace-label">TELEMETRY</p>

          <h2>Telemetry</h2>
        </header>

        <p className="telemetry-no-sensor">Register or select a sensor to submit telemetry.</p>
      </section>
    );
  }

  return (
    <section className="telemetry-workspace" aria-labelledby="telemetry-workspace-title">
      <header className="telemetry-workspace-heading">
        <p className="telemetry-workspace-label">TELEMETRY</p>

        <h2 id="telemetry-workspace-title">Simulator</h2>
      </header>

      <div className="telemetry-workspace-grid">
        <TelemetrySimulator sensor={sensor} onTelemetryAccepted={handleTelemetryAccepted} />

        <TelemetryHistory
          sensor={sensor}
          readings={readings}
          state={historyState}
          errorMessage={historyError}
          onRetry={() => {
            void loadTelemetry(sensor.id);
          }}
        />
      </div>

      {/*
       * Using the selected sensor ID as a React key resets the editable batch
       * state when the operator changes sensors without requiring another
       * state-resetting effect.
       */}
      <TelemetryBatchSimulator
        key={sensor.id}
        sensor={sensor}
        onBatchAccepted={handleBatchAccepted}
      />
    </section>
  );
}

export default TelemetryWorkspace;
