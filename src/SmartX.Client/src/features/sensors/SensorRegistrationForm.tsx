import { useState, type FormEvent } from 'react';

import { ApiError, getApiErrorMessage } from '../../api/apiTypes';
import {
  identifierKinds,
  registerSensor,
  sensorCategories,
  telemetryDataTypes,
  type IdentifierKind,
  type SensorCategory,
  type SensorResponse,
  type TelemetryDataType,
} from '../../api/sensorsApi';
import {
  createInitialSensorRegistrationValues,
  hasSensorRegistrationErrors,
  toRegisterSensorRequest,
  validateSensorRegistration,
  type SensorRegistrationField,
  type SensorRegistrationFormValues,
} from './sensorValidation';

interface SensorRegistrationFormProps {
  readonly onRegistered: (sensor: SensorResponse) => void;
}

/**
 * Tracks feedback for the most recent registration attempt.
 */
type SubmissionState =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'success'; message: string }
  | { kind: 'error'; title: string; message: string };

/**
 * Field order is also used when moving keyboard focus to the first invalid
 * value after an unsuccessful client-side submission.
 */
const registrationFields: readonly SensorRegistrationField[] = [
  'name',
  'identifierKind',
  'deviceIdentifier',
  'facility',
  'zone',
  'nodeId',
  'category',
  'telemetryDataType',
];

/**
 * DOM IDs associated with each registration field.
 */
const fieldElementIds: Record<SensorRegistrationField, string> = {
  name: 'sensor-name',
  identifierKind: 'sensor-identifier-kind',
  deviceIdentifier: 'sensor-device-identifier',
  facility: 'sensor-facility',
  zone: 'sensor-zone',
  nodeId: 'sensor-node-id',
  category: 'sensor-category',
  telemetryDataType: 'sensor-telemetry-data-type',
};

/**
 * Returns validation messages supplied by ASP.NET Core model validation when
 * available.
 */
function getProblemValidationMessages(error: ApiError): readonly string[] {
  if (!error.problem?.errors) {
    return [];
  }

  return Object.values(error.problem.errors).flat();
}

/**
 * Converts API failures into concise registration feedback while preserving
 * details returned by the backend.
 */
function getRegistrationFailure(error: unknown): {
  title: string;
  message: string;
} {
  if (error instanceof ApiError) {
    const validationMessages = getProblemValidationMessages(error);

    const backendMessage =
      validationMessages.length > 0
        ? validationMessages.join(' ')
        : getApiErrorMessage(error, 'The sensor could not be registered.');

    if (error.status === 409) {
      return {
        title: 'Duplicate identifier',
        message: backendMessage || 'A sensor with this device identifier is already registered.',
      };
    }

    return {
      title: 'Registration failed',
      message: backendMessage,
    };
  }

  return {
    title: 'Registration failed',
    message: 'The Smart-X API could not complete the registration request.',
  };
}

/**
 * Displays one field validation message.
 */
function FieldError({ id, message }: { readonly id: string; readonly message?: string }) {
  if (!message) {
    return null;
  }

  return (
    <p className="sensor-field-error" id={id}>
      {message}
    </p>
  );
}

/**
 * Provides immediate client-side validation and submits sensor registration
 * requests to the existing Smart-X API.
 */
function SensorRegistrationForm({ onRegistered }: SensorRegistrationFormProps) {
  const [values, setValues] = useState<SensorRegistrationFormValues>(
    createInitialSensorRegistrationValues,
  );

  const [touched, setTouched] = useState<Partial<Record<SensorRegistrationField, boolean>>>({});

  const [submissionState, setSubmissionState] = useState<SubmissionState>({
    kind: 'idle',
  });

  /*
   * Validation is derived directly from current form values rather than being
   * duplicated inside state. Once a field has been touched, its feedback
   * therefore updates immediately as the user continues typing.
   */
  const validationErrors = validateSensorRegistration(values);

  /**
   * Updates one strongly typed form field.
   */
  function updateField<K extends SensorRegistrationField>(
    field: K,
    value: SensorRegistrationFormValues[K],
  ): void {
    setValues((currentValues) => ({
      ...currentValues,
      [field]: value,
    }));

    /*
     * Previous submission feedback no longer necessarily describes the
     * current form after the user edits a value.
     */
    if (submissionState.kind !== 'idle') {
      setSubmissionState({
        kind: 'idle',
      });
    }
  }

  /**
   * Marks one field as interacted with so validation can be shown without
   * overwhelming untouched form controls on first render.
   */
  function markTouched(field: SensorRegistrationField): void {
    setTouched((currentTouched) => ({
      ...currentTouched,
      [field]: true,
    }));
  }

  /**
   * Returns an error only after the user has interacted with the field.
   */
  function getVisibleError(field: SensorRegistrationField): string | undefined {
    if (!touched[field]) {
      return undefined;
    }

    return validationErrors[field];
  }

  /**
   * Moves focus to the first invalid field after a rejected submission.
   */
  function focusFirstInvalidField(): void {
    const firstInvalidField = registrationFields.find((field) => Boolean(validationErrors[field]));

    if (!firstInvalidField) {
      return;
    }

    window.requestAnimationFrame(() => {
      document.getElementById(fieldElementIds[firstInvalidField])?.focus();
    });
  }

  /**
   * Validates and submits the sensor registration request.
   */
  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();

    const allTouched = Object.fromEntries(
      registrationFields.map((field) => [field, true]),
    ) as Record<SensorRegistrationField, boolean>;

    setTouched(allTouched);

    if (hasSensorRegistrationErrors(validationErrors)) {
      setSubmissionState({
        kind: 'error',
        title: 'Check registration details',
        message: 'Correct the highlighted fields before submitting.',
      });

      focusFirstInvalidField();
      return;
    }

    setSubmissionState({
      kind: 'submitting',
    });

    try {
      const sensor = await registerSensor(toRegisterSensorRequest(values));

      /*
       * Inform the parent workspace immediately so the new sensor can be
       * displayed and selected without requiring a second API request.
       */
      onRegistered(sensor);

      setValues(createInitialSensorRegistrationValues());
      setTouched({});

      setSubmissionState({
        kind: 'success',
        message: `${sensor.name} registered successfully.`,
      });
    } catch (error) {
      const failure = getRegistrationFailure(error);

      setSubmissionState({
        kind: 'error',
        title: failure.title,
        message: failure.message,
      });
    }
  }

  const deviceIdentifierError = getVisibleError('deviceIdentifier');

  return (
    <section className="sensor-panel" aria-labelledby="register-sensor-title">
      <div className="sensor-panel-heading">
        <h3 id="register-sensor-title">Register sensor</h3>
      </div>

      <form
        className="sensor-registration-form"
        noValidate
        onSubmit={(event) => {
          void handleSubmit(event);
        }}
      >
        <div className="sensor-form-grid">
          <div className="sensor-field sensor-field--full">
            <label htmlFor="sensor-name">Sensor name</label>

            <input
              id="sensor-name"
              name="name"
              type="text"
              value={values.name}
              maxLength={100}
              required
              autoComplete="off"
              aria-invalid={Boolean(getVisibleError('name'))}
              aria-describedby={getVisibleError('name') ? 'sensor-name-error' : undefined}
              onBlur={() => {
                markTouched('name');
              }}
              onChange={(event) => {
                updateField('name', event.target.value);
              }}
            />

            <FieldError id="sensor-name-error" message={getVisibleError('name')} />
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-identifier-kind">Identifier type</label>

            <select
              id="sensor-identifier-kind"
              name="identifierKind"
              value={values.identifierKind}
              required
              onBlur={() => {
                markTouched('identifierKind');
              }}
              onChange={(event) => {
                updateField('identifierKind', event.target.value as IdentifierKind);
              }}
            >
              {identifierKinds.map((kind) => (
                <option key={kind} value={kind}>
                  {kind === 'MacAddress' ? 'MAC address' : 'Unique identifier'}
                </option>
              ))}
            </select>
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-device-identifier">
              {values.identifierKind === 'MacAddress' ? 'MAC address' : 'Unique identifier'}
            </label>

            <input
              id="sensor-device-identifier"
              name="deviceIdentifier"
              type="text"
              value={values.deviceIdentifier}
              maxLength={values.identifierKind === 'MacAddress' ? 17 : 64}
              required
              autoComplete="off"
              spellCheck={false}
              placeholder={
                values.identifierKind === 'MacAddress' ? 'AA:BB:CC:DD:EE:FF' : 'sensor-node-01'
              }
              aria-invalid={Boolean(deviceIdentifierError)}
              aria-describedby={
                deviceIdentifierError ? 'sensor-device-identifier-error' : undefined
              }
              onBlur={() => {
                markTouched('deviceIdentifier');
              }}
              onChange={(event) => {
                updateField('deviceIdentifier', event.target.value);
              }}
            />

            <FieldError id="sensor-device-identifier-error" message={deviceIdentifierError} />
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-facility">Facility</label>

            <input
              id="sensor-facility"
              name="facility"
              type="text"
              value={values.facility}
              maxLength={80}
              required
              autoComplete="off"
              aria-invalid={Boolean(getVisibleError('facility'))}
              aria-describedby={getVisibleError('facility') ? 'sensor-facility-error' : undefined}
              onBlur={() => {
                markTouched('facility');
              }}
              onChange={(event) => {
                updateField('facility', event.target.value);
              }}
            />

            <FieldError id="sensor-facility-error" message={getVisibleError('facility')} />
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-zone">Zone</label>

            <input
              id="sensor-zone"
              name="zone"
              type="text"
              value={values.zone}
              maxLength={80}
              required
              autoComplete="off"
              aria-invalid={Boolean(getVisibleError('zone'))}
              aria-describedby={getVisibleError('zone') ? 'sensor-zone-error' : undefined}
              onBlur={() => {
                markTouched('zone');
              }}
              onChange={(event) => {
                updateField('zone', event.target.value);
              }}
            />

            <FieldError id="sensor-zone-error" message={getVisibleError('zone')} />
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-node-id">Node ID</label>

            <input
              id="sensor-node-id"
              name="nodeId"
              type="text"
              value={values.nodeId}
              maxLength={80}
              required
              autoComplete="off"
              spellCheck={false}
              aria-invalid={Boolean(getVisibleError('nodeId'))}
              aria-describedby={getVisibleError('nodeId') ? 'sensor-node-id-error' : undefined}
              onBlur={() => {
                markTouched('nodeId');
              }}
              onChange={(event) => {
                updateField('nodeId', event.target.value);
              }}
            />

            <FieldError id="sensor-node-id-error" message={getVisibleError('nodeId')} />
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-category">Category</label>

            <select
              id="sensor-category"
              name="category"
              value={values.category}
              required
              onChange={(event) => {
                updateField('category', event.target.value as SensorCategory);
              }}
            >
              {sensorCategories.map((category) => (
                <option key={category} value={category}>
                  {category === 'PowerConsumption' ? 'Power consumption' : category}
                </option>
              ))}
            </select>
          </div>

          <div className="sensor-field">
            <label htmlFor="sensor-telemetry-data-type">Telemetry type</label>

            <select
              id="sensor-telemetry-data-type"
              name="telemetryDataType"
              value={values.telemetryDataType}
              required
              onChange={(event) => {
                updateField('telemetryDataType', event.target.value as TelemetryDataType);
              }}
            >
              {telemetryDataTypes.map((dataType) => (
                <option key={dataType} value={dataType}>
                  {dataType}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="sensor-form-footer">
          <button
            className="sensor-primary-action"
            type="submit"
            disabled={submissionState.kind === 'submitting'}
          >
            {submissionState.kind === 'submitting' ? 'Registering...' : 'Register sensor'}
          </button>

          {submissionState.kind === 'success' && (
            <div className="sensor-feedback sensor-feedback--success" role="status">
              {submissionState.message}
            </div>
          )}

          {submissionState.kind === 'error' && (
            <div className="sensor-feedback sensor-feedback--error" role="alert">
              <strong>{submissionState.title}</strong>
              <span>{submissionState.message}</span>
            </div>
          )}
        </div>
      </form>
    </section>
  );
}

export default SensorRegistrationForm;
