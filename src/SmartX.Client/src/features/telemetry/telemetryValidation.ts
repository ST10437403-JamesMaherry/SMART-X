import type { TelemetryDataType } from '../../api/sensorsApi';
import type { IngestTelemetryRequest } from '../../api/telemetryApi';

/**
 * .NET Int32 boundaries used by the backend Integer telemetry contract.
 *
 * TypeScript numbers can represent values well outside this range, so the
 * frontend explicitly applies the same transport limitation before sending a
 * request to ASP.NET Core.
 */
const minimumInt32Value = -2_147_483_648;
const maximumInt32Value = 2_147_483_647;

/**
 * Values entered into the telemetry simulator before conversion into the
 * strongly typed Smart-X API request.
 */
export interface TelemetryFormValues {
  telemetryType: TelemetryDataType;
  numericValue: string;
  booleanValue: 'true' | 'false';
}

/**
 * Validation errors displayed by the simulator.
 */
export interface TelemetryFormErrors {
  telemetryType?: string;
  value?: string;
}

/**
 * Creates telemetry form state matched to the selected sensor.
 */
export function createTelemetryFormValues(telemetryType: TelemetryDataType): TelemetryFormValues {
  return {
    telemetryType,
    numericValue: '',
    booleanValue: 'true',
  };
}

/**
 * Validates a telemetry value against both the user-selected type and the
 * telemetry type configured on the selected sensor.
 *
 * The backend remains authoritative, but preventing an obviously invalid
 * request here gives the operator immediate feedback.
 */
export function validateTelemetryForm(
  values: TelemetryFormValues,
  configuredTelemetryType: TelemetryDataType,
): TelemetryFormErrors {
  const errors: TelemetryFormErrors = {};

  if (values.telemetryType !== configuredTelemetryType) {
    errors.telemetryType = `This sensor accepts ${configuredTelemetryType} telemetry.`;
  }

  if (values.telemetryType === 'Boolean') {
    return errors;
  }

  const rawValue = values.numericValue.trim();

  if (!rawValue) {
    errors.value = 'A telemetry value is required.';
    return errors;
  }

  const parsedValue = Number(rawValue);

  if (!Number.isFinite(parsedValue)) {
    errors.value = 'Enter a valid finite numeric value.';
    return errors;
  }

  if (values.telemetryType === 'Integer') {
    if (!Number.isInteger(parsedValue)) {
      errors.value = 'Integer telemetry must contain a whole number.';
      return errors;
    }

    if (parsedValue < minimumInt32Value || parsedValue > maximumInt32Value) {
      errors.value = 'Integer telemetry must fit within the .NET Int32 range.';
    }
  }

  return errors;
}

/**
 * Returns true when telemetry validation produced at least one error.
 */
export function hasTelemetryErrors(errors: TelemetryFormErrors): boolean {
  return Object.keys(errors).length > 0;
}

/**
 * Converts validated simulator values into the exact payload contract expected
 * by POST /api/sensors/{id}/telemetry.
 *
 * Exactly one telemetry value property is populated. Legitimate values such
 * as numeric zero and boolean false therefore remain distinct from null.
 */
export function toTelemetryRequest(values: TelemetryFormValues): IngestTelemetryRequest {
  const capturedAtUtc = new Date().toISOString();

  if (values.telemetryType === 'Float') {
    return {
      capturedAtUtc,
      floatValue: Number(values.numericValue),
      integerValue: null,
      booleanValue: null,
    };
  }

  if (values.telemetryType === 'Integer') {
    return {
      capturedAtUtc,
      floatValue: null,
      integerValue: Number(values.numericValue),
      booleanValue: null,
    };
  }

  return {
    capturedAtUtc,
    floatValue: null,
    integerValue: null,
    booleanValue: values.booleanValue === 'true',
  };
}
