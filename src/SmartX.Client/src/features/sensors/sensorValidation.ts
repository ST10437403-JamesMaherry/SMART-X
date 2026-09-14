import type {
  IdentifierKind,
  RegisterSensorRequest,
  SensorCategory,
  TelemetryDataType,
} from '../../api/sensorsApi';

/**
 * Maximum values mirror the Smart-X backend domain invariants.
 *
 * The backend remains the final source of truth, but duplicating these rules
 * on the client allows users to receive immediate feedback before an invalid
 * request is transmitted.
 */
const maxSensorNameLength = 100;
const maxDeploymentSegmentLength = 80;
const maxUniqueIdentifierLength = 64;

/**
 * Matches the DeviceIdentifier MAC-address rule implemented by the domain.
 *
 * Accepted examples include:
 * - AA:BB:CC:DD:EE:FF
 * - AA-BB-CC-DD-EE-FF
 * - AABBCCDDEEFF
 */
const macAddressPattern = /^(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})$/;

/**
 * Matches the Smart-X custom identifier rule implemented by the backend.
 *
 * The first character must be alphanumeric. Remaining characters may also
 * contain dots, underscores and hyphens.
 */
const uniqueIdentifierPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/**
 * Detects C0 and C1 control characters without embedding control-character
 * ranges inside a regular expression.
 *
 * This mirrors the practical control-character values that can reach browser
 * text fields while remaining compatible with the project's ESLint rules.
 */
function containsControlCharacter(value: string): boolean {
  return Array.from(value).some((character) => {
    const codePoint = character.codePointAt(0);

    if (codePoint === undefined) {
      return false;
    }

    const isC0Control = codePoint >= 0x00 && codePoint <= 0x1f;
    const isC1Control = codePoint >= 0x7f && codePoint <= 0x9f;

    return isC0Control || isC1Control;
  });
}

/**
 * Flat form representation used by the React registration interface.
 *
 * Keeping location fields flat makes individual validation and accessible
 * error association straightforward. The values are converted into the nested
 * backend request contract immediately before submission.
 */
export interface SensorRegistrationFormValues {
  name: string;
  identifierKind: IdentifierKind;
  deviceIdentifier: string;
  facility: string;
  zone: string;
  nodeId: string;
  category: SensorCategory;
  telemetryDataType: TelemetryDataType;
}

export type SensorRegistrationField = keyof SensorRegistrationFormValues;

export type SensorRegistrationErrors = Partial<Record<SensorRegistrationField, string>>;

/**
 * Creates a fresh default form state.
 */
export function createInitialSensorRegistrationValues(): SensorRegistrationFormValues {
  return {
    name: '',
    identifierKind: 'MacAddress',
    deviceIdentifier: '',
    facility: '',
    zone: '',
    nodeId: '',
    category: 'Environmental',
    telemetryDataType: 'Float',
  };
}

/**
 * Validates a required general text field against the corresponding backend
 * length and control-character invariants.
 */
function validateRequiredText(
  value: string,
  requiredMessage: string,
  maxLength: number,
  maxLengthMessage: string,
  controlCharacterMessage: string,
): string | undefined {
  const normalizedValue = value.trim();

  if (!normalizedValue) {
    return requiredMessage;
  }

  if (normalizedValue.length > maxLength) {
    return maxLengthMessage;
  }

  if (containsControlCharacter(normalizedValue)) {
    return controlCharacterMessage;
  }

  return undefined;
}

/**
 * Validates the complete sensor registration form using rules matched to the
 * existing Smart-X domain model.
 */
export function validateSensorRegistration(
  values: SensorRegistrationFormValues,
): SensorRegistrationErrors {
  const errors: SensorRegistrationErrors = {};

  const nameError = validateRequiredText(
    values.name,
    'A sensor profile name is required.',
    maxSensorNameLength,
    `A sensor profile name cannot exceed ${maxSensorNameLength} characters.`,
    'A sensor profile name contains invalid control characters.',
  );

  if (nameError) {
    errors.name = nameError;
  }

  const identifier = values.deviceIdentifier.trim();

  if (values.identifierKind === 'MacAddress') {
    if (!identifier) {
      errors.deviceIdentifier = 'A device MAC address is required.';
    } else if (!macAddressPattern.test(identifier)) {
      errors.deviceIdentifier = `'${identifier}' is not a valid 48-bit MAC address.`;
    }
  } else if (!identifier) {
    errors.deviceIdentifier = 'A unique device identifier is required.';
  } else if (identifier.length > maxUniqueIdentifierLength) {
    errors.deviceIdentifier =
      `A unique device identifier cannot exceed ` + `${maxUniqueIdentifierLength} characters.`;
  } else if (!uniqueIdentifierPattern.test(identifier)) {
    errors.deviceIdentifier =
      'A unique device identifier may only contain letters, digits, dots, ' +
      'hyphens and underscores, and must begin with a letter or digit.';
  }

  const facilityError = validateRequiredText(
    values.facility,
    'Deployment location Facility is required.',
    maxDeploymentSegmentLength,
    `Deployment location Facility cannot exceed ${maxDeploymentSegmentLength} characters.`,
    'Deployment location Facility contains invalid control characters.',
  );

  if (facilityError) {
    errors.facility = facilityError;
  }

  const zoneError = validateRequiredText(
    values.zone,
    'Deployment location Zone is required.',
    maxDeploymentSegmentLength,
    `Deployment location Zone cannot exceed ${maxDeploymentSegmentLength} characters.`,
    'Deployment location Zone contains invalid control characters.',
  );

  if (zoneError) {
    errors.zone = zoneError;
  }

  const nodeIdError = validateRequiredText(
    values.nodeId,
    'Deployment location NodeId is required.',
    maxDeploymentSegmentLength,
    `Deployment location NodeId cannot exceed ${maxDeploymentSegmentLength} characters.`,
    'Deployment location NodeId contains invalid control characters.',
  );

  if (nodeIdError) {
    errors.nodeId = nodeIdError;
  }

  return errors;
}

/**
 * Returns true when at least one client-side validation error exists.
 */
export function hasSensorRegistrationErrors(errors: SensorRegistrationErrors): boolean {
  return Object.keys(errors).length > 0;
}

/**
 * Converts the flat UI model into the exact transport contract expected by
 * POST /api/sensors.
 *
 * Text values are trimmed before transport while MAC canonicalisation remains
 * the responsibility of the backend domain model.
 */
export function toRegisterSensorRequest(
  values: SensorRegistrationFormValues,
): RegisterSensorRequest {
  return {
    name: values.name.trim(),
    deviceIdentifier: values.deviceIdentifier.trim(),
    identifierKind: values.identifierKind,
    deploymentLocation: {
      facility: values.facility.trim(),
      zone: values.zone.trim(),
      nodeId: values.nodeId.trim(),
    },
    category: values.category,
    telemetryDataType: values.telemetryDataType,
  };
}
