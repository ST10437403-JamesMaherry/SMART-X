import { useState, type FormEvent } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import type { SensorResponse } from '../../api/sensorsApi';
import { ingestTelemetryBatch, type TelemetryBatchIngestionResponse } from '../../api/telemetryApi';
import {
  createInitialTelemetryBatchRows,
  createSampleTelemetryBatchRows,
  createTelemetryBatchRow,
  hasTelemetryBatchErrors,
  parseTelemetryBatchRows,
  toTelemetryBatchRequest,
  type TelemetryBatchRow,
} from './telemetryBatch';

interface TelemetryBatchSimulatorProps {
  readonly sensor: SensorResponse;
  readonly onBatchAccepted: () => void;
}

type BatchSubmissionState =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | {
      kind: 'success';
      response: TelemetryBatchIngestionResponse;
    }
  | { kind: 'error'; message: string };

/**
 * Returns a concise example suited to the selected strongly typed sensor.
 */
function getBatchPlaceholder(telemetryType: SensorResponse['telemetryDataType']): string {
  if (telemetryType === 'Float') {
    return '21.4, 21.8, 22.1';
  }

  if (telemetryType === 'Integer') {
    return '4100, 4200, 4150';
  }

  return 'true, false, true';
}

/**
 * Allows an operator to construct and submit ordered jagged historical
 * telemetry batches to the selected Smart-X sensor.
 *
 * Each visible editor row becomes one inner array in the API request. Rows
 * may contain different numbers of readings, including zero readings, which
 * makes the jagged-array behaviour directly observable from the frontend.
 */
function TelemetryBatchSimulator({ sensor, onBatchAccepted }: TelemetryBatchSimulatorProps) {
  const [rows, setRows] = useState<readonly TelemetryBatchRow[]>(createInitialTelemetryBatchRows);

  const [touchedRows, setTouchedRows] = useState<Readonly<Record<string, boolean>>>({});

  const [submissionState, setSubmissionState] = useState<BatchSubmissionState>({
    kind: 'idle',
  });

  const parseResult = parseTelemetryBatchRows(rows, sensor.telemetryDataType);

  const batchShape = parseResult.batches.map((batch) => batch.values.length);

  /**
   * Updates one batch row while preserving the surrounding jagged structure.
   */
  function updateRow(rowId: string, rawValues: string): void {
    setRows((currentRows) =>
      currentRows.map((row) =>
        row.id === rowId
          ? {
              ...row,
              rawValues,
            }
          : row,
      ),
    );

    setSubmissionState({
      kind: 'idle',
    });
  }

  /**
   * Adds another inner array to the end of the historical batch request.
   */
  function addBatch(): void {
    setRows((currentRows) => [...currentRows, createTelemetryBatchRow()]);

    setSubmissionState({
      kind: 'idle',
    });
  }

  /**
   * Removes an inner batch.
   *
   * At least one visible batch row is retained so the editor always has an
   * obvious place for new telemetry input. An empty row itself remains a valid
   * zero-length batch.
   */
  function removeBatch(rowId: string): void {
    if (rows.length <= 1) {
      return;
    }

    setRows((currentRows) => currentRows.filter((row) => row.id !== rowId));

    setTouchedRows((currentTouchedRows) => {
      const nextTouchedRows = {
        ...currentTouchedRows,
      };

      delete nextTouchedRows[rowId];

      return nextTouchedRows;
    });

    setSubmissionState({
      kind: 'idle',
    });
  }

  /**
   * Loads an assessment-friendly uneven sample:
   *
   * [2, 1, 0, 3]
   *
   * The empty third batch is intentional.
   */
  function loadSample(): void {
    setRows(createSampleTelemetryBatchRows(sensor.telemetryDataType));

    setTouchedRows({});

    setSubmissionState({
      kind: 'idle',
    });
  }

  /**
   * Marks one row as interacted with so errors continue updating immediately
   * as the operator corrects the value.
   */
  function markRowTouched(rowId: string): void {
    setTouchedRows((currentTouchedRows) => ({
      ...currentTouchedRows,
      [rowId]: true,
    }));
  }

  /**
   * Focuses the first invalid batch after an unsuccessful submit.
   */
  function focusFirstInvalidBatch(): void {
    const firstInvalidRow = rows.find((row) => Boolean(parseResult.errors[row.id]));

    if (!firstInvalidRow) {
      return;
    }

    window.requestAnimationFrame(() => {
      document.getElementById(`telemetry-batch-${firstInvalidRow.id}`)?.focus();
    });
  }

  /**
   * Validates and submits the complete jagged request.
   */
  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();

    setTouchedRows(Object.fromEntries(rows.map((row) => [row.id, true])));

    if (hasTelemetryBatchErrors(parseResult)) {
      setSubmissionState({
        kind: 'error',
        message: 'Correct the invalid batch values before submitting.',
      });

      focusFirstInvalidBatch();
      return;
    }

    setSubmissionState({
      kind: 'submitting',
    });

    try {
      const response = await ingestTelemetryBatch(
        sensor.id,
        toTelemetryBatchRequest(parseResult, sensor.telemetryDataType),
      );

      setSubmissionState({
        kind: 'success',
        response,
      });

      /*
       * Batch responses contain summary information rather than individual
       * packets, so refresh the recent-history view after successful storage.
       */
      onBatchAccepted();
    } catch (error) {
      setSubmissionState({
        kind: 'error',
        message: getApiErrorMessage(
          error,
          'The historical telemetry batches could not be submitted.',
        ),
      });
    }
  }

  const isSubmitting = submissionState.kind === 'submitting';

  return (
    <section className="telemetry-batch-simulator" aria-labelledby="telemetry-batch-title">
      <header className="telemetry-batch-heading">
        <div>
          <p className="telemetry-workspace-label">HISTORICAL TELEMETRY</p>

          <h3 id="telemetry-batch-title">Batch simulator</h3>
        </div>

        {!hasTelemetryBatchErrors(parseResult) && (
          <output
            className="telemetry-batch-shape"
            aria-label={`Jagged batch lengths: ${batchShape.join(', ')}`}
          >
            [{batchShape.join(', ')}]
          </output>
        )}
      </header>

      <form
        className="telemetry-batch-form"
        noValidate
        onSubmit={(event) => {
          void handleSubmit(event);
        }}
      >
        <fieldset className="telemetry-batch-fieldset" disabled={isSubmitting}>
          <legend className="visually-hidden">Historical telemetry batches</legend>

          <div className="telemetry-batch-toolbar">
            <button className="telemetry-batch-secondary-action" type="button" onClick={loadSample}>
              Load sample
            </button>

            <button className="telemetry-batch-secondary-action" type="button" onClick={addBatch}>
              Add batch
            </button>
          </div>

          <div className="telemetry-batch-list">
            {rows.map((row, index) => {
              const inputId = `telemetry-batch-${row.id}`;

              const error = touchedRows[row.id] ? parseResult.errors[row.id] : undefined;

              const errorId = `${inputId}-error`;

              return (
                <div className="telemetry-batch-row" key={row.id}>
                  <label htmlFor={inputId}>Batch {String(index + 1).padStart(2, '0')}</label>

                  <div className="telemetry-batch-input-group">
                    <input
                      id={inputId}
                      type="text"
                      value={row.rawValues}
                      autoComplete="off"
                      spellCheck={false}
                      placeholder={getBatchPlaceholder(sensor.telemetryDataType)}
                      aria-invalid={Boolean(error)}
                      aria-describedby={error ? errorId : undefined}
                      onBlur={() => {
                        markRowTouched(row.id);
                      }}
                      onChange={(event) => {
                        updateRow(row.id, event.target.value);
                      }}
                    />

                    {error && (
                      <p className="telemetry-field-error" id={errorId}>
                        {error}
                      </p>
                    )}
                  </div>

                  <button
                    className="telemetry-batch-remove"
                    type="button"
                    disabled={rows.length === 1}
                    aria-label={`Remove batch ${index + 1}`}
                    onClick={() => {
                      removeBatch(row.id);
                    }}
                  >
                    Remove
                  </button>
                </div>
              );
            })}
          </div>

          <div className="telemetry-batch-footer">
            <button className="telemetry-primary-action" type="submit">
              {isSubmitting ? 'Submitting...' : 'Submit batches'}
            </button>

            {submissionState.kind === 'success' && (
              <p className="telemetry-feedback telemetry-feedback--success" role="status">
                {submissionState.response.batchCount} batches /{' '}
                {submissionState.response.acceptedPacketCount} packets accepted.
              </p>
            )}

            {submissionState.kind === 'error' && (
              <p className="telemetry-feedback telemetry-feedback--error" role="alert">
                {submissionState.message}
              </p>
            )}
          </div>
        </fieldset>
      </form>
    </section>
  );
}

export default TelemetryBatchSimulator;
