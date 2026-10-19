import { getJson, postJson } from './apiClient';
import type { DeviceSearchResponse } from './deviceSearchApi';

/** Only deliberate search/filter actions are allowed through this endpoint. */
export type SubmittedSearchInteractionKind = 'SearchSubmitted' | 'FiltersChanged';

export interface OperatorInteractionResponse {
  readonly sequence: number;
  readonly occurredAtUtc: string;
  readonly kind:
    | SubmittedSearchInteractionKind
    | 'DeviceViewed'
    | 'AlertViewed'
    | 'CommandExecuted'
    | 'CommandUndone';
  readonly deviceId: string | null;
  readonly alertEpisodeId: string | null;
  readonly commandId: string | null;
  readonly searchText: string | null;
  readonly canonicalIdentifier: string | null;
  readonly category: string | null;
  readonly status: string | null;
  readonly hasActiveCondition: boolean | null;
  readonly conditionType: string | null;
  readonly resultCount: number | null;
}

export interface OperatorInteractionHistoryResponse {
  readonly totalRecorded: number;
  readonly retainedCount: number;
  readonly capacity: number;
  readonly events: readonly OperatorInteractionResponse[];
}

interface RecordDeviceSearchRequest {
  readonly kind: SubmittedSearchInteractionKind;
  readonly deviceIdentifier: string | null;
  readonly identifierKind: string | null;
  readonly text: string | null;
  readonly category: string | null;
  readonly status: string | null;
  readonly hasActiveCondition: string | null;
  readonly conditionType: string | null;
  readonly limit: string;
  readonly offset: string;
  readonly selectedDeviceId: string | null;
  readonly sourceAlertEpisodeId: string | null;
}

interface RecordOperatorViewRequest {
  readonly deviceId: string;
  readonly alertEpisodeId: string | null;
}

/**
 * A POST is made only from an actual Submit/Apply/Associate button click.
 * GET results are deliberately not treated as evidence. The server reruns
 * and validates the query (and result-page membership if a device is given).
 */
export function recordSearchInteraction(
  kind: SubmittedSearchInteractionKind,
  response: DeviceSearchResponse,
  selectedDeviceId: string | null = null,
  sourceAlertEpisodeId: string | null = null,
): Promise<OperatorInteractionResponse> {
  const applied = response.appliedFilters;
  const payload: RecordDeviceSearchRequest = {
    kind,
    deviceIdentifier: applied.deviceIdentifier,
    identifierKind: applied.identifierKind,
    text: applied.text,
    category: applied.category,
    status: applied.status,
    hasActiveCondition:
      applied.hasActiveCondition === null ? null : String(applied.hasActiveCondition),
    conditionType: applied.conditionType,
    limit: String(response.limit),
    offset: String(response.offset),
    selectedDeviceId,
    sourceAlertEpisodeId,
  };
  return postJson<OperatorInteractionResponse, RecordDeviceSearchRequest>(
    '/api/realtime/interactions/search',
    payload,
  );
}

/** Explicit views only. Never call this during an HTTP or SignalR refresh. */
export function recordOperatorView(
  deviceId: string,
  alertEpisodeId: string | null = null,
): Promise<OperatorInteractionResponse> {
  return postJson<OperatorInteractionResponse, RecordOperatorViewRequest>(
    '/api/realtime/interactions/view',
    { deviceId, alertEpisodeId },
  );
}

/** Read-only inspection for tests and later predictive-engine work. */
export function getOperatorInteractionHistory(
  limit = 50,
  signal?: AbortSignal,
): Promise<OperatorInteractionHistoryResponse> {
  return getJson<OperatorInteractionHistoryResponse>(
    `/api/realtime/interactions?limit=${limit}`,
    signal,
  );
}
