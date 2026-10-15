import { useMemo, useState, type ReactNode } from 'react';

import { initialDeviceSearchFilters, type DeviceSearchFilters } from '../api/deviceSearchApi';
import { createInitialSensorRegistrationValues } from '../features/sensors/sensorValidation';
import {
  WorkspaceStateContext,
  type RegistrationDraft,
  type TelemetryBatchDraft,
  type TelemetryDraft,
  type WorkspaceState,
} from './workspaceState';

interface WorkspaceStateProviderProps {
  readonly children: ReactNode;
}

/**
 * Lives above route elements so navigation does not discard selection or
 * safe drafts. Refreshing the browser starts a new workspace session.
 */
function WorkspaceStateProvider({ children }: WorkspaceStateProviderProps) {
  const [selectedSensorId, setSelectedSensorId] = useState<string | null>(null);

  // Store only search criteria, never result rows or command/alert data.
  // The provider survives route transitions but resets on full-page reload.
  const [deviceSearchDraft, setDeviceSearchDraft] = useState<DeviceSearchFilters>(
    initialDeviceSearchFilters,
  );
  const [deviceSearchApplied, setDeviceSearchApplied] = useState<DeviceSearchFilters>(
    initialDeviceSearchFilters,
  );
  const [deviceSearchOffset, setDeviceSearchOffset] = useState(0);
  const [sourceAlertEpisodeId, setSourceAlertEpisodeId] = useState<string | null>(null);

  const [registrationDraft, setRegistrationDraft] = useState<RegistrationDraft>(() => ({
    values: createInitialSensorRegistrationValues(),
    touched: {},
    submissionState: { kind: 'idle' },
  }));

  const [telemetryDrafts, setTelemetryDrafts] = useState<Readonly<Record<string, TelemetryDraft>>>(
    {},
  );

  const [batchDrafts, setBatchDrafts] = useState<Readonly<Record<string, TelemetryBatchDraft>>>({});

  const value = useMemo<WorkspaceState>(
    () => ({
      selectedSensorId,
      setSelectedSensorId,
      deviceSearchDraft,
      setDeviceSearchDraft,
      deviceSearchApplied,
      setDeviceSearchApplied,
      deviceSearchOffset,
      setDeviceSearchOffset,
      sourceAlertEpisodeId,
      setSourceAlertEpisodeId,
      registrationDraft,
      setRegistrationDraft,
      telemetryDrafts,
      setTelemetryDrafts,
      batchDrafts,
      setBatchDrafts,
    }),
    [
      selectedSensorId,
      deviceSearchDraft,
      deviceSearchApplied,
      deviceSearchOffset,
      sourceAlertEpisodeId,
      registrationDraft,
      telemetryDrafts,
      batchDrafts,
    ],
  );

  return <WorkspaceStateContext.Provider value={value}>{children}</WorkspaceStateContext.Provider>;
}

export default WorkspaceStateProvider;
