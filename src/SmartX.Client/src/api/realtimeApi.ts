import { getJson, putJson } from './apiClient';
import type { ActuatorCommandStateResponse, CommandAuditResponse } from './commandsApi';
import type {
  DashboardIssueSeverity,
  DashboardIssueState,
  DashboardIssueType,
} from './dashboardApi';
import type {
  IdentifierKind,
  SensorCategory,
  SensorDeploymentLocation,
  SensorStatus,
  TelemetryDataType,
} from './sensorsApi';
import type { TelemetryReadingResponse } from './telemetryApi';

export type ProcessingLane = 'Ordinary' | 'Critical';
export type HistoryOrder = 'asc' | 'desc';

/** One actual P2-03 worker dispatch, not a synthetic client queue item. */
export interface TelemetryDispatchResponse {
  readonly enqueueSequence: number;
  readonly dequeueSequence: number;
  readonly lane: ProcessingLane;
  readonly severity: DashboardIssueSeverity;
  readonly telemetry: TelemetryReadingResponse;
  readonly acceptedAtUtc: string;
  readonly startedAtUtc: string;
  readonly finishedAtUtc: string | null;
  readonly outcome: string;
  readonly updatedLatestObservation: boolean;
}

/**
 * Process-lifetime counts come from the worker and actual FIFO/priority buffer.
 * recentDispatches is a bounded diagnostic window, not the full history store.
 */
export interface TelemetryProcessingResponse {
  readonly acceptedCount: number;
  readonly dispatchedCount: number;
  readonly processedCount: number;
  readonly ordinaryPendingCount: number;
  readonly criticalPendingCount: number;
  readonly pendingCount: number;
  readonly outstandingDispatchedCount: number;
  readonly ordinaryDispatchPaused: boolean;
  readonly workerStatus: string;
  readonly failure: string | null;
  readonly currentDispatch: TelemetryDispatchResponse | null;
  readonly recentDispatches: readonly TelemetryDispatchResponse[];
}

/** A canonical registered sensor joined with *completed* processing state. */
export interface RealtimeDeviceResponse {
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
  readonly latestProcessedTelemetry: TelemetryReadingResponse | null;
  readonly lastProcessedAtUtc: string | null;
  readonly commandedActuator: ActuatorCommandStateResponse | null;
}

/** Authoritative device and worker snapshot, generated in the backend. */
export interface RealtimeSnapshotResponse {
  readonly generatedAtUtc: string;
  readonly deviceCount: number;
  readonly devices: readonly RealtimeDeviceResponse[];
  readonly processing: TelemetryProcessingResponse;
}

/** One unresolved condition, indexed by the P2-06 sensor/type HashSet key. */
export interface RealtimeActiveConditionResponse {
  readonly membershipKey: string;
  readonly issueId: string;
  readonly sensorProfileId: string;
  readonly conditionType: DashboardIssueType;
  readonly severity: DashboardIssueSeverity;
  readonly state: DashboardIssueState;
  readonly occurrenceCount: number;
  readonly priorityScore: number;
  readonly firstObservedAtUtc: string;
  readonly lastObservedAtUtc: string;
  readonly acknowledgedAtUtc: string | null;
}

export interface RealtimeActiveConditionsResponse {
  readonly generatedAtUtc: string;
  readonly activeConditionCount: number;
  readonly unacknowledgedConditionCount: number;
  readonly activeConditions: readonly RealtimeActiveConditionResponse[];
}

/** One actual SortedDictionary event: telemetry OR command/reversal. */
export interface RealtimeHistoryEventResponse {
  readonly eventId: string;
  readonly targetDeviceId: string;
  readonly kind: 'TelemetryProcessed' | 'RelayCommand' | 'RelayUndo';
  readonly occurredAtUtc: string;
  readonly recordedAtUtc: string;
  readonly sequence: number;
  readonly telemetry: TelemetryReadingResponse | null;
  readonly command: CommandAuditResponse | null;
  readonly enqueueSequence: number | null;
  readonly dequeueSequence: number | null;
  readonly processingLane: ProcessingLane | null;
  readonly severity: DashboardIssueSeverity | null;
  readonly acceptedAtUtc: string | null;
}

/** The newest limited events, ordered by occurrence timestamps. */
export interface RealtimeHistoryResponse {
  readonly targetDeviceId: string;
  readonly totalRecordedEvents: number;
  readonly retainedEventCount: number;
  readonly retentionCapacity: number;
  readonly limit: number;
  readonly order: 'Ascending' | 'Descending';
  readonly events: readonly RealtimeHistoryEventResponse[];
}

/** SignalR sends invalidations; HTTP remains the source of truth. */
export interface RealtimeChangedResponse {
  readonly reason: string;
  readonly sensorProfileId: string | null;
  readonly occurredAtUtc: string;
}

/** Reads all devices together with actual processing diagnostics. */
export function getRealtimeSnapshot(signal?: AbortSignal): Promise<RealtimeSnapshotResponse> {
  return getJson<RealtimeSnapshotResponse>('/api/realtime/snapshot', signal);
}

/** Reads the backend's canonical device registry (not a client-side copy). */
export function getRealtimeDevices(
  signal?: AbortSignal,
): Promise<readonly RealtimeDeviceResponse[]> {
  return getJson<readonly RealtimeDeviceResponse[]>('/api/realtime/devices', signal);
}

/** Reads current actual worker and queue state. */
export function getRealtimeProcessing(signal?: AbortSignal): Promise<TelemetryProcessingResponse> {
  return getJson<TelemetryProcessingResponse>('/api/realtime/processing', signal);
}

/** Shares the Part 1 Adaptive Anomaly Spotlight's HashSet-backed lifecycle. */
export function getRealtimeActiveConditions(
  signal?: AbortSignal,
): Promise<RealtimeActiveConditionsResponse> {
  return getJson<RealtimeActiveConditionsResponse>(
    '/api/realtime/snapshot/active-conditions',
    signal,
  );
}

/** Read-only, bounded chronological history for one registered device. */
export function getRealtimeHistory(
  deviceId: string,
  limit = 100,
  order: HistoryOrder = 'asc',
  signal?: AbortSignal,
): Promise<RealtimeHistoryResponse> {
  if (!Number.isInteger(limit) || limit < 1 || limit > 500) {
    throw new RangeError('History limit must be an integer between 1 and 500.');
  }

  const query = new URLSearchParams({ limit: String(limit), order });
  return getJson<RealtimeHistoryResponse>(
    `/api/realtime/devices/${encodeURIComponent(deviceId)}/history?${query.toString()}`,
    signal,
  );
}

/**
 * Simulation-only global control. Pausing ordinary work does not stop intake
 * or the critical priority lane. The returned state is authoritative.
 */
export function setOrdinaryDispatchPaused(
  paused: boolean,
  signal?: AbortSignal,
): Promise<TelemetryProcessingResponse> {
  return putJson<TelemetryProcessingResponse, { paused: boolean }>(
    '/api/realtime/simulation/ordinary-dispatch',
    { paused },
    signal,
  );
}
