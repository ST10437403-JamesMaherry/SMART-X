import type { FormEvent } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  telemetryDataTypes,
  type SensorResponse,
  type TelemetryDataType,
} from '../../api/sensorsApi';
import { ingestTelemetry, type TelemetryReadingResponse } from '../../api/telemetryApi';
import { useDraftField, useTelemetryDraft } from '../../state/useWorkspaceDrafts';
import {
  hasTelemetryErrors,
  toTelemetryRequest,
  validateTelemetryForm,
} from './telemetryValidation';

interface TelemetrySimulatorProps {
  readonly sensor: SensorResponse;
  readonly onTelemetryAccepted: (reading: TelemetryReadingResponse) => void;
}

/**
 * Converts transport telemetry type names into concise labels.
 */
function formatTelemetryType(telemetryType: TelemetryDataType): string {
  if (telemetryType === 'Float') {
    return 'Decimal';
  }

  return telemetryType;
}

/**
 * Simulates one telemetry packet for the currently selected Smart-X sensor.
 *
 * Operators may inspect and choose a payload type, while client-side
 * validation prevents submission when that choice conflicts with the sensor's
 * registered telemetry contract.
 */
function TelemetrySimulator({ sensor, onTelemetryAccepted }: TelemetrySimulatorProps) {
  const [draft, setDraft] = useTelemetryDraft(sensor);

  const [values, setValues] = useDraftField(draft, setDraft, 'values');

  const [touched, setTouched] = useDraftField(draft, setDraft, 'touched');

  const [submissionState, setSubmissionState] = useDraftField(draft, setDraft, 'submissionState');

  const validationErrors = validateTelemetryForm(values, sensor.telemetryDataType);

  /**
   * Updates the selected telemetry type and clears stale submission feedback.
   */
  function handleTelemetryTypeChange(telemetryType: TelemetryDataType): void {
    setValues((currentValues) => ({
      ...currentValues,
      telemetryType,
      numericValue: '',
    }));

    setTouched(false);
    setSubmissionState({
      kind: 'idle',
    });
  }

  /**
   * Submits one strongly typed telemetry reading to the selected sensor.
   */
  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();

    if (submissionState.kind === 'submitting') {
      return;
    }

    setTouched(true);

    if (hasTelemetryErrors(validationErrors)) {
      setSubmissionState({
        kind: 'error',
        message: 'Correct the telemetry value before submitting.',
      });

      return;
    }

    setSubmissionState({
      kind: 'submitting',
    });

    try {
      const reading = await ingestTelemetry(sensor.id, toTelemetryRequest(values));

      onTelemetryAccepted(reading);

      setValues((currentValues) => ({
        ...currentValues,
        numericValue: '',
      }));

      setTouched(false);

      setSubmissionState({
        kind: 'success',
        message: 'Telemetry accepted.',
      });
    } catch (error) {
      setSubmissionState({
        kind: 'error',
        message: getApiErrorMessage(error, 'The telemetry packet could not be submitted.'),
      });
    }
  }

  const valueError = touched ? validationErrors.value : undefined;

  const typeError =
    values.telemetryType !== sensor.telemetryDataType ? validationErrors.telemetryType : undefined;

  return (
    <section className="telemetry-panel" aria-labelledby="telemetry-simulator-title">
      <header className="telemetry-panel-heading">
        <h3 id="telemetry-simulator-title">Telemetry simulator</h3>
      </header>

      <div className="telemetry-sensor-context">
        <strong>{sensor.name}</strong>
        <span>{sensor.deviceIdentifier}</span>
      </div>

      <form
        className="telemetry-form"
        noValidate
        onSubmit={(event) => {
          void handleSubmit(event);
        }}
      >
        <div className="telemetry-field">
          <label htmlFor="telemetry-type">Telemetry type</label>

          <select
            id="telemetry-type"
            name="telemetryType"
            disabled={submissionState.kind === 'submitting'}
            value={values.telemetryType}
            aria-invalid={Boolean(typeError)}
            aria-describedby={typeError ? 'telemetry-type-error' : undefined}
            onChange={(event) => {
              handleTelemetryTypeChange(event.target.value as TelemetryDataType);
            }}
          >
            {telemetryDataTypes.map((telemetryType) => (
              <option key={telemetryType} value={telemetryType}>
                {formatTelemetryType(telemetryType)}
              </option>
            ))}
          </select>

          {typeError && (
            <p className="telemetry-field-error" id="telemetry-type-error">
              {typeError}
            </p>
          )}
        </div>

        {values.telemetryType === 'Boolean' ? (
          <div className="telemetry-field">
            <label htmlFor="telemetry-boolean-value">Value</label>

            <select
              id="telemetry-boolean-value"
              name="booleanValue"
              disabled={submissionState.kind === 'submitting'}
              value={values.booleanValue}
              onChange={(event) => {
                setValues((currentValues) => ({
                  ...currentValues,
                  booleanValue: event.target.value as 'true' | 'false',
                }));

                setSubmissionState({
                  kind: 'idle',
                });
              }}
            >
              <option value="true">True</option>
              <option value="false">False</option>
            </select>
          </div>
        ) : (
          <div className="telemetry-field">
            <label htmlFor="telemetry-numeric-value">Value</label>

            <input
              id="telemetry-numeric-value"
              name="numericValue"
              disabled={submissionState.kind === 'submitting'}
              type="number"
              step={values.telemetryType === 'Integer' ? '1' : 'any'}
              inputMode="decimal"
              value={values.numericValue}
              aria-invalid={Boolean(valueError)}
              aria-describedby={valueError ? 'telemetry-value-error' : undefined}
              onBlur={() => {
                setTouched(true);
              }}
              onChange={(event) => {
                setValues((currentValues) => ({
                  ...currentValues,
                  numericValue: event.target.value,
                }));

                setSubmissionState({
                  kind: 'idle',
                });
              }}
            />

            {valueError && (
              <p className="telemetry-field-error" id="telemetry-value-error">
                {valueError}
              </p>
            )}
          </div>
        )}

        <div className="telemetry-form-footer">
          <button
            className="telemetry-primary-action"
            type="submit"
            disabled={submissionState.kind === 'submitting'}
          >
            {submissionState.kind === 'submitting' ? 'Submitting...' : 'Submit reading'}
          </button>

          {submissionState.kind === 'success' && (
            <p className="telemetry-feedback telemetry-feedback--success" role="status">
              {submissionState.message}
            </p>
          )}

          {submissionState.kind === 'error' && (
            <p className="telemetry-feedback telemetry-feedback--error" role="alert">
              {submissionState.message}
            </p>
          )}
        </div>
      </form>
    </section>
  );
}

export default TelemetrySimulator;
