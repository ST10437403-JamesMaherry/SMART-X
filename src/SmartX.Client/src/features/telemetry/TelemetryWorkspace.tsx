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
 * Maximum number of recent telemetry readings shown in the Part 1 client.
 */
const recentTelemetryLimit = 50;

/**
 * Owns recent telemetry state for the currently selected Smart-X sensor.
 *
 * Individual readings and historical jagged batches share this workspace so
 * accepted packets immediately feed one authoritative recent-history view.
 */
function TelemetryWorkspace({ sensor }: TelemetryWorkspaceProps) {
  const [readings, setReadings] = useState<readonly TelemetryReadingResponse[]>([]);

  const [historyState, setHistoryState] = useState<'loading' | 'ready' | 'error'>('ready');

  const [historyError, setHistoryError] = useState<string | null>(null);

  /**
   * Retrieves recent telemetry for one sensor.
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

    const loadTimer = window.setTimeout(() => {
      void loadTelemetry(sensor.id, controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [loadTelemetry, sensor]);

  /**
   * Adds one API-confirmed telemetry packet directly to visible history.
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
   * Historical batch submission returns aggregate information rather than the
   * individual packets, so refresh from the authoritative API afterwards.
   */
  function handleBatchAccepted(): void {
    if (!sensor) {
      return;
    }

    void loadTelemetry(sensor.id);
  }

  if (!sensor) {
    return (
      <section id="telemetry-management" className="telemetry-workspace" tabIndex={-1}>
        <header className="telemetry-workspace-heading">
          <p className="telemetry-workspace-label">TELEMETRY</p>

          <h2>Telemetry</h2>
        </header>

        <p className="telemetry-no-sensor">Register or select a sensor to submit telemetry.</p>
      </section>
    );
  }

  return (
    <section
      id="telemetry-management"
      className="telemetry-workspace"
      aria-labelledby="telemetry-workspace-title"
      tabIndex={-1}
    >
      <header className="telemetry-workspace-heading">
        <p className="telemetry-workspace-label">TELEMETRY</p>

        <h2 id="telemetry-workspace-title">Simulator</h2>
      </header>

      <div className="telemetry-workspace-grid">
        <TelemetrySimulator
          key={`${sensor.id}:${sensor.telemetryDataType}`}
          sensor={sensor}
          onTelemetryAccepted={handleTelemetryAccepted}
        />

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

      <TelemetryBatchSimulator
        key={`${sensor.id}:${sensor.telemetryDataType}`}
        sensor={sensor}
        onBatchAccepted={handleBatchAccepted}
      />
    </section>
  );
}

export default TelemetryWorkspace;
