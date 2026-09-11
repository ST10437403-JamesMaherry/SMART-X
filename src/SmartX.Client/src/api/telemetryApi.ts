import { getJson, postJson } from './apiClient';

import type { TelemetryDataType } from './sensorsApi';

/**
 * API request representing one strongly typed telemetry reading.
 *
 * Exactly one payload field should contain a value and it must correspond to
 * the telemetry type configured on the selected sensor.
 */
export interface IngestTelemetryRequest {
  readonly capturedAtUtc: string;
  readonly floatValue: number | null;
  readonly integerValue: number | null;
  readonly booleanValue: boolean | null;
}

/**
 * Historical telemetry is deliberately represented as a jagged collection.
 *
 * Individual batches may contain different numbers of readings. The backend
 * transfers this raw structure into its custom HistoricalTelemetryBuffer<T>.
 */
export interface IngestTelemetryBatchRequest {
  readonly batches: readonly (readonly (IngestTelemetryRequest | null)[] | null)[];
}

/**
 * Summary returned after successful historical batch processing.
 */
export interface TelemetryBatchIngestionResponse {
  readonly sensorProfileId: string;
  readonly telemetryDataType: TelemetryDataType;
  readonly batchCount: number;
  readonly acceptedPacketCount: number;
}

/**
 * One stored telemetry reading returned by the Smart-X API.
 *
 * Only the payload property associated with telemetryDataType will contain a
 * value. The remaining payload properties are null.
 */
export interface TelemetryReadingResponse {
  readonly sensorProfileId: string;
  readonly deviceIdentifier: string;
  readonly telemetryDataType: TelemetryDataType;
  readonly floatValue: number | null;
  readonly integerValue: number | null;
  readonly booleanValue: boolean | null;
  readonly capturedAtUtc: string;
}

/**
 * Submits one telemetry reading to a registered sensor.
 */
export function ingestTelemetry(
  sensorId: string,
  request: IngestTelemetryRequest,
  signal?: AbortSignal,
): Promise<TelemetryReadingResponse> {
  return postJson<TelemetryReadingResponse, IngestTelemetryRequest>(
    `/api/sensors/${encodeURIComponent(sensorId)}/telemetry`,
    request,
    signal,
  );
}

/**
 * Submits ordered historical telemetry batches.
 */
export function ingestTelemetryBatch(
  sensorId: string,
  request: IngestTelemetryBatchRequest,
  signal?: AbortSignal,
): Promise<TelemetryBatchIngestionResponse> {
  return postJson<TelemetryBatchIngestionResponse, IngestTelemetryBatchRequest>(
    `/api/sensors/${encodeURIComponent(sensorId)}/telemetry/batches`,
    request,
    signal,
  );
}

/**
 * Retrieves the newest stored telemetry readings for one sensor.
 */
export function getRecentTelemetry(
  sensorId: string,
  limit = 50,
  signal?: AbortSignal,
): Promise<readonly TelemetryReadingResponse[]> {
  return getJson<readonly TelemetryReadingResponse[]>(
    `/api/sensors/${encodeURIComponent(sensorId)}/telemetry?limit=${limit}`,
    signal,
  );
}
