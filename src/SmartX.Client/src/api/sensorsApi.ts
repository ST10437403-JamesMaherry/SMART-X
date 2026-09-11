import { getJson, postJson, putJson } from './apiClient';

/**
 * Transport values supported by the Smart-X sensor identifier contract.
 */
export const identifierKinds = ['MacAddress', 'UniqueIdentifier'] as const;

export type IdentifierKind = (typeof identifierKinds)[number];

/**
 * Sensor categories supported by the Part 1 backend.
 */
export const sensorCategories = ['Environmental', 'PowerConsumption', 'Actuator'] as const;

export type SensorCategory = (typeof sensorCategories)[number];

/**
 * Strongly typed telemetry categories supported by registered sensors.
 */
export const telemetryDataTypes = ['Float', 'Integer', 'Boolean'] as const;

export type TelemetryDataType = (typeof telemetryDataTypes)[number];

/**
 * Operational sensor states currently exposed by the domain model.
 */
export const sensorStatuses = [
  'Offline',
  'Online',
  'Warning',
  'Fault',
  'Maintenance',
  'Decommissioned',
] as const;

export type SensorStatus = (typeof sensorStatuses)[number];

/**
 * Physical deployment hierarchy supplied during sensor registration.
 */
export interface SensorDeploymentLocation {
  readonly facility: string;
  readonly zone: string;
  readonly nodeId: string;
}

/**
 * Request sent when registering a new Smart-X sensor.
 */
export interface RegisterSensorRequest {
  readonly name: string;
  readonly deviceIdentifier: string;
  readonly identifierKind: IdentifierKind;
  readonly deploymentLocation: SensorDeploymentLocation;
  readonly category: SensorCategory;
  readonly telemetryDataType: TelemetryDataType;
}

/**
 * Request used when changing mutable sensor profile details.
 *
 * Device identity is intentionally absent because the backend treats it as
 * immutable after registration.
 */
export interface UpdateSensorRequest {
  readonly name: string;
  readonly deploymentLocation: SensorDeploymentLocation;
  readonly category: SensorCategory;
  readonly telemetryDataType: TelemetryDataType;
}

/**
 * API representation of one registered Smart-X sensor.
 */
export interface SensorResponse {
  readonly id: string;
  readonly name: string;
  readonly deviceIdentifier: string;
  readonly identifierKind: IdentifierKind;
  readonly deploymentLocation: SensorDeploymentLocation;
  readonly category: SensorCategory;
  readonly status: SensorStatus;
  readonly telemetryDataType: TelemetryDataType;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/**
 * Registers one validated Smart-X sensor profile.
 */
export function registerSensor(
  request: RegisterSensorRequest,
  signal?: AbortSignal,
): Promise<SensorResponse> {
  return postJson<SensorResponse, RegisterSensorRequest>('/api/sensors', request, signal);
}

/**
 * Retrieves every sensor currently registered in the Part 1 runtime.
 */
export function getSensors(signal?: AbortSignal): Promise<readonly SensorResponse[]> {
  return getJson<readonly SensorResponse[]>('/api/sensors', signal);
}

/**
 * Retrieves one sensor using its internal Smart-X identifier.
 */
export function getSensor(sensorId: string, signal?: AbortSignal): Promise<SensorResponse> {
  return getJson<SensorResponse>(`/api/sensors/${encodeURIComponent(sensorId)}`, signal);
}

/**
 * Updates the mutable details of an existing Smart-X sensor profile.
 */
export function updateSensor(
  sensorId: string,
  request: UpdateSensorRequest,
  signal?: AbortSignal,
): Promise<SensorResponse> {
  return putJson<SensorResponse, UpdateSensorRequest>(
    `/api/sensors/${encodeURIComponent(sensorId)}`,
    request,
    signal,
  );
}
