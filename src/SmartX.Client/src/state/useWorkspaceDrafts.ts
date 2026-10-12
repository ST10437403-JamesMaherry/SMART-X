import { useCallback, useState, type Dispatch, type SetStateAction } from 'react';

import type { SensorResponse } from '../api/sensorsApi';
import { createInitialTelemetryBatchRows } from '../features/telemetry/telemetryBatch';
import { createTelemetryFormValues } from '../features/telemetry/telemetryValidation';
import { useWorkspaceState, type TelemetryBatchDraft, type TelemetryDraft } from './workspaceState';

/**
 * Binds one editor to a device and its telemetry contract. A changed contract
 * gets a fresh draft instead of reusing a payload for the previous type.
 * Callers key the editor by the same device/type pair.
 */
function useDeviceDraft<T>(
  key: string,
  createInitialDraft: () => T,
  drafts: Readonly<Record<string, T>>,
  setDrafts: Dispatch<SetStateAction<Readonly<Record<string, T>>>>,
): readonly [T, Dispatch<SetStateAction<T>>] {
  const [initialDraft] = useState(createInitialDraft);

  const setDraft = useCallback(
    (action: SetStateAction<T>): void => {
      setDrafts((currentDrafts) => {
        const currentDraft = currentDrafts[key] ?? initialDraft;

        const nextDraft =
          typeof action === 'function' ? (action as (previous: T) => T)(currentDraft) : action;

        return { ...currentDrafts, [key]: nextDraft };
      });
    },
    [initialDraft, key, setDrafts],
  );

  return [drafts[key] ?? initialDraft, setDraft];
}

export function useTelemetryDraft(sensor: SensorResponse) {
  const { telemetryDrafts, setTelemetryDrafts } = useWorkspaceState();

  return useDeviceDraft<TelemetryDraft>(
    `${sensor.id}:${sensor.telemetryDataType}`,
    () => ({
      values: createTelemetryFormValues(sensor.telemetryDataType),
      touched: false,
      submissionState: { kind: 'idle' },
    }),
    telemetryDrafts,
    setTelemetryDrafts,
  );
}

export function useTelemetryBatchDraft(sensor: SensorResponse) {
  const { batchDrafts, setBatchDrafts } = useWorkspaceState();

  return useDeviceDraft<TelemetryBatchDraft>(
    `${sensor.id}:${sensor.telemetryDataType}`,
    () => ({
      rows: createInitialTelemetryBatchRows(),
      touchedRows: {},
      submissionState: { kind: 'idle' },
    }),
    batchDrafts,
    setBatchDrafts,
  );
}

/**
 * Gives existing forms a normal React field setter while storing the complete
 * draft above the routes. Functional updates also settle pending submissions
 * correctly if the operator leaves the page before a response arrives.
 */
export function useDraftField<T, K extends keyof T>(
  draft: T,
  setDraft: Dispatch<SetStateAction<T>>,
  field: K,
): readonly [T[K], Dispatch<SetStateAction<T[K]>>] {
  const setField = useCallback(
    (action: SetStateAction<T[K]>): void => {
      setDraft((currentDraft) => ({
        ...currentDraft,
        [field]:
          typeof action === 'function'
            ? (action as (previous: T[K]) => T[K])(currentDraft[field])
            : action,
      }));
    },
    [field, setDraft],
  );

  return [draft[field], setField];
}
