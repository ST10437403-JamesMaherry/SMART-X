import { useState, type FormEvent } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { changeSensorStatus, type SensorResponse, type SensorStatus } from '../../api/sensorsApi';

interface SensorStateSimulatorProps {
  readonly sensor: SensorResponse;
  readonly onSensorUpdated: (sensor: SensorResponse) => void;
}

/**
 * Part 1 operational states useful for demonstrating the Adaptive Anomaly
 * Spotlight.
 *
 * Decommissioned is deliberately excluded from the simulator because it is a
 * terminal lifecycle state rather than a temporary operational condition.
 */
const simulatedStatuses: readonly SensorStatus[] = [
  'Offline',
  'Online',
  'Warning',
  'Fault',
  'Maintenance',
];

type StateUpdateFeedback =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'success'; message: string }
  | { kind: 'error'; message: string };

/**
 * Provides a controlled Part 1 simulation mechanism for sensor state changes.
 *
 * The actual transition is performed by the .NET API and Domain model. This
 * component merely exposes that operation so disconnect and fault behaviour
 * can be demonstrated without physical IoT hardware.
 */
function SensorStateSimulator({ sensor, onSensorUpdated }: SensorStateSimulatorProps) {
  const initialStatus: SensorStatus =
    sensor.status === 'Decommissioned' ? 'Offline' : sensor.status;

  const [targetStatus, setTargetStatus] = useState<SensorStatus>(initialStatus);

  const [feedback, setFeedback] = useState<StateUpdateFeedback>({
    kind: 'idle',
  });

  /**
   * Sends one operational-state transition to the authoritative API.
   */
  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();

    if (targetStatus === sensor.status) {
      return;
    }

    setFeedback({
      kind: 'submitting',
    });

    try {
      const updatedSensor = await changeSensorStatus(sensor.id, targetStatus);

      onSensorUpdated(updatedSensor);

      setTargetStatus(updatedSensor.status);

      setFeedback({
        kind: 'success',
        message: `State updated to ${updatedSensor.status}.`,
      });
    } catch (error) {
      setFeedback({
        kind: 'error',
        message: getApiErrorMessage(error, 'The sensor state could not be changed.'),
      });
    }
  }

  const isSubmitting = feedback.kind === 'submitting';

  if (sensor.status === 'Decommissioned') {
    return (
      <section className="spotlight-state-simulator" aria-labelledby="spotlight-state-title">
        <div className="spotlight-state-context">
          <p className="spotlight-state-label">SENSOR STATE</p>

          <h3 id="spotlight-state-title">{sensor.name}</h3>

          <span>{sensor.deviceIdentifier}</span>
        </div>

        <p className="spotlight-state-terminal">Decommissioned is a terminal sensor state.</p>
      </section>
    );
  }

  return (
    <section className="spotlight-state-simulator" aria-labelledby="spotlight-state-title">
      <div className="spotlight-state-context">
        <p className="spotlight-state-label">SENSOR STATE</p>

        <h3 id="spotlight-state-title">{sensor.name}</h3>

        <span>Current state: {sensor.status}</span>
      </div>

      <form
        className="spotlight-state-form"
        onSubmit={(event) => {
          void handleSubmit(event);
        }}
      >
        <div className="spotlight-state-field">
          <label htmlFor="dashboard-sensor-status">Simulated state</label>

          <select
            id="dashboard-sensor-status"
            value={targetStatus}
            disabled={isSubmitting}
            onChange={(event) => {
              setTargetStatus(event.target.value as SensorStatus);

              setFeedback({
                kind: 'idle',
              });
            }}
          >
            {simulatedStatuses.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </div>

        <button
          className="spotlight-secondary-action"
          type="submit"
          disabled={isSubmitting || targetStatus === sensor.status}
        >
          {isSubmitting ? 'Updating...' : 'Apply state'}
        </button>

        {feedback.kind === 'success' && (
          <p className="spotlight-action-feedback spotlight-action-feedback--success" role="status">
            {feedback.message}
          </p>
        )}

        {feedback.kind === 'error' && (
          <p className="spotlight-action-feedback spotlight-action-feedback--error" role="alert">
            {feedback.message}
          </p>
        )}
      </form>
    </section>
  );
}

export default SensorStateSimulator;
