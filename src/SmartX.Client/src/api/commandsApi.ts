import { getJson, postJson } from './apiClient';

/** Only these two simulated relay operations are accepted by Smart-X. */
export type RelayCommandType = 'RelayOn' | 'RelayOff';
export type RelayState = 'On' | 'Off';

/** A committed command, an accepted no-op, or a genuine linked reversal. */
export interface CommandAuditResponse {
  readonly commandId: string;
  readonly targetDeviceId: string;
  readonly commandType: RelayCommandType;
  readonly executedAtUtc: string;
  readonly previousState: RelayState;
  readonly resultingState: RelayState;
  readonly stateChanged: boolean;
  readonly isReversal: boolean;
  readonly reversesCommandId: string | null;
}

/** The authoritative commanded state and per-device Stack metadata. */
export interface ActuatorCommandStateResponse {
  readonly targetDeviceId: string;
  readonly commandedState: RelayState;
  readonly latestUndoableCommandId: string | null;
  readonly undoDepth: number;
}

export interface RelayCommandResponse {
  readonly command: CommandAuditResponse;
  readonly actuator: ActuatorCommandStateResponse;
}

/** Audit ordering is the backend's accepted-command order, not event time. */
export interface ActuatorCommandHistoryResponse {
  readonly actuator: ActuatorCommandStateResponse;
  readonly totalAuditCount: number;
  readonly recentAudit: readonly CommandAuditResponse[];
}

function commandsPath(deviceId: string): string {
  return `/api/realtime/devices/${encodeURIComponent(deviceId)}/commands`;
}

/** Reads existing audit and Stack state; reading never executes a command. */
export function getActuatorCommandHistory(
  deviceId: string,
  signal?: AbortSignal,
): Promise<ActuatorCommandHistoryResponse> {
  return getJson<ActuatorCommandHistoryResponse>(commandsPath(deviceId), signal);
}

/** Executes one real command using the validated backend command service. */
export function executeRelayCommand(
  deviceId: string,
  commandType: RelayCommandType,
  signal?: AbortSignal,
): Promise<RelayCommandResponse> {
  return postJson<RelayCommandResponse, { commandType: RelayCommandType }>(
    commandsPath(deviceId),
    { commandType },
    signal,
  );
}

/**
 * Performs optimistic-concurrency checked Undo against the real device Stack.
 * The caller must use latestUndoableCommandId from an authoritative snapshot.
 */
export function undoRelayCommand(
  deviceId: string,
  expectedLatestCommandId: string,
  signal?: AbortSignal,
): Promise<RelayCommandResponse> {
  return postJson<RelayCommandResponse, { expectedLatestCommandId: string }>(
    `${commandsPath(deviceId)}/undo`,
    { expectedLatestCommandId },
    signal,
  );
}
