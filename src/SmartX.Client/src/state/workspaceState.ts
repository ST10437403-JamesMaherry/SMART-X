import { createContext, useContext, type Dispatch, type SetStateAction } from 'react';

import type { DeviceSearchFilters } from '../api/deviceSearchApi';
import type { TelemetryBatchIngestionResponse } from '../api/telemetryApi';
import type {
  SensorRegistrationField,
  SensorRegistrationFormValues,
} from '../features/sensors/sensorValidation';
import type { TelemetryBatchRow } from '../features/telemetry/telemetryBatch';
import type { TelemetryFormValues } from '../features/telemetry/telemetryValidation';

type RegistrationSubmissionState =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'success'; message: string }
  | { kind: 'error'; title: string; message: string };

type TelemetrySubmissionState =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'success'; message: string }
  | { kind: 'error'; message: string };

type BatchSubmissionState =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'success'; response: TelemetryBatchIngestionResponse }
  | { kind: 'error'; message: string };

export interface RegistrationDraft {
  readonly values: SensorRegistrationFormValues;
  readonly touched: Partial<Record<SensorRegistrationField, boolean>>;
  readonly submissionState: RegistrationSubmissionState;
}

export interface TelemetryDraft {
  readonly values: TelemetryFormValues;
  readonly touched: boolean;
  readonly submissionState: TelemetrySubmissionState;
}

/**
 * Safe AVL range inputs only. No result rows, packet payloads, cursors,
 * attachment bytes or pending actuator commands may be persisted here.
 */
export interface TopologyHistoryDraft {
  readonly fromLocal: string;
  readonly toLocal: string;
  readonly pageSize: number;
}

export interface TelemetryBatchDraft {
  readonly rows: readonly TelemetryBatchRow[];
  readonly touchedRows: Readonly<Record<string, boolean>>;
  readonly submissionState: BatchSubmissionState;
}

/**
 * Application-lifetime UI state only. Server records are fetched by their
 * existing workspaces; attachment files never enter this context.
 */
export interface WorkspaceState {
  readonly selectedSensorId: string | null;
  readonly setSelectedSensorId: Dispatch<SetStateAction<string | null>>;
  /** Safe search input and the last server-validated submitted filters. */
  readonly deviceSearchDraft: DeviceSearchFilters;
  readonly setDeviceSearchDraft: Dispatch<SetStateAction<DeviceSearchFilters>>;
  readonly deviceSearchApplied: DeviceSearchFilters;
  readonly setDeviceSearchApplied: Dispatch<SetStateAction<DeviceSearchFilters>>;
  readonly deviceSearchOffset: number;
  readonly setDeviceSearchOffset: Dispatch<SetStateAction<number>>;
  /** Explicitly inspected unresolved alert episode for cross-device discovery. */
  readonly sourceAlertEpisodeId: string | null;
  readonly setSourceAlertEpisodeId: Dispatch<SetStateAction<string | null>>;
  /** Per-sensor history filters only; oldest drafts are bounded by the editor. */
  readonly topologyHistoryDrafts: Readonly<Record<string, TopologyHistoryDraft>>;
  readonly setTopologyHistoryDrafts: Dispatch<
    SetStateAction<Readonly<Record<string, TopologyHistoryDraft>>>
  >;
  readonly registrationDraft: RegistrationDraft;
  readonly setRegistrationDraft: Dispatch<SetStateAction<RegistrationDraft>>;
  readonly telemetryDrafts: Readonly<Record<string, TelemetryDraft>>;
  readonly setTelemetryDrafts: Dispatch<SetStateAction<Readonly<Record<string, TelemetryDraft>>>>;
  readonly batchDrafts: Readonly<Record<string, TelemetryBatchDraft>>;
  readonly setBatchDrafts: Dispatch<SetStateAction<Readonly<Record<string, TelemetryBatchDraft>>>>;
}

// Keep context and hooks separate from the component for Vite Fast Refresh.
export const WorkspaceStateContext = createContext<WorkspaceState | undefined>(undefined);

export function useWorkspaceState(): WorkspaceState {
  const context = useContext(WorkspaceStateContext);

  if (!context) {
    throw new Error('Workspace state must be used inside WorkspaceStateProvider.');
  }

  return context;
}
