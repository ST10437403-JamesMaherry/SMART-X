import { getJson } from './apiClient';
import type { DashboardIssueType } from './dashboardApi';
import type { RealtimeDeviceResponse } from './realtimeApi';
import type { IdentifierKind, SensorCategory, SensorStatus } from './sensorsApi';

/**
 * The application provider stores only safe search criteria. Server-owned
 * devices, active-condition membership, and query results are never copied
 * into persistent client state.
 */
export interface DeviceSearchFilters {
  readonly deviceIdentifier: string;
  readonly identifierKind: IdentifierKind;
  readonly text: string;
  readonly category: SensorCategory | '';
  readonly status: SensorStatus | '';
  readonly hasActiveCondition: '' | 'true' | 'false';
  readonly conditionType: DashboardIssueType | '';
}

export const initialDeviceSearchFilters: DeviceSearchFilters = {
  deviceIdentifier: '',
  identifierKind: 'MacAddress',
  text: '',
  category: '',
  status: '',
  hasActiveCondition: '',
  conditionType: '',
};

export const deviceSearchPageSize = 25;

/** The actual P2-08 query response, including one live alert episode per row. */
export interface DeviceSearchItemResponse {
  readonly device: RealtimeDeviceResponse;
  readonly activeConditionCount: number;
  readonly highestPriorityIssueId: string | null;
  readonly highestPriorityConditionType: DashboardIssueType | null;
}

export interface DeviceSearchAppliedFiltersResponse {
  readonly deviceIdentifier: string | null;
  readonly identifierKind: IdentifierKind | null;
  readonly text: string | null;
  readonly category: SensorCategory | null;
  readonly status: SensorStatus | null;
  readonly hasActiveCondition: boolean | null;
  readonly conditionType: DashboardIssueType | null;
}

export interface DeviceSearchResponse {
  readonly generatedAtUtc: string;
  readonly totalMatches: number;
  readonly limit: number;
  readonly offset: number;
  readonly hasMore: boolean;
  readonly appliedFilters: DeviceSearchAppliedFiltersResponse;
  readonly devices: readonly DeviceSearchItemResponse[];
}

/**
 * Submit only populated criteria. In particular, identifierKind is omitted
 * for text-only searches, because the backend requires both exact fields
 * together. The backend performs the authoritative validation/normalisation.
 */
export function buildDeviceSearchPath(
  filters: DeviceSearchFilters,
  limit = deviceSearchPageSize,
  offset = 0,
): string {
  const query = new URLSearchParams();
  const identifier = filters.deviceIdentifier.trim();
  const text = filters.text.trim();

  if (identifier) {
    query.set('deviceIdentifier', identifier);
    query.set('identifierKind', filters.identifierKind);
  }
  if (text) query.set('text', text);
  if (filters.category) query.set('category', filters.category);
  if (filters.status) query.set('status', filters.status);
  if (filters.hasActiveCondition) query.set('hasActiveCondition', filters.hasActiveCondition);
  if (filters.conditionType) query.set('conditionType', filters.conditionType);

  query.set('limit', String(limit));
  query.set('offset', String(offset));
  return `/api/realtime/devices/search?${query.toString()}`;
}

/** Read-only GET. SignalR refreshes, typing and paging never create events. */
export function getDeviceSearch(
  filters: DeviceSearchFilters,
  limit = deviceSearchPageSize,
  offset = 0,
  signal?: AbortSignal,
): Promise<DeviceSearchResponse> {
  return getJson<DeviceSearchResponse>(buildDeviceSearchPath(filters, limit, offset), signal);
}

/** Retain the backend-normalised applied query when the operator navigates. */
export function filtersFromSearchResponse(response: DeviceSearchResponse): DeviceSearchFilters {
  const criteria = response.appliedFilters;
  return {
    deviceIdentifier: criteria.deviceIdentifier ?? '',
    identifierKind: criteria.identifierKind ?? 'MacAddress',
    text: criteria.text ?? '',
    category: criteria.category ?? '',
    status: criteria.status ?? '',
    hasActiveCondition:
      criteria.hasActiveCondition === null
        ? ''
        : (String(criteria.hasActiveCondition) as 'true' | 'false'),
    conditionType: criteria.conditionType ?? '',
  };
}

/** Normalise the draft only for detecting an explicit no-op filter change. */
export function sameDeviceSearchFilters(a: DeviceSearchFilters, b: DeviceSearchFilters): boolean {
  return (
    a.deviceIdentifier.trim() === b.deviceIdentifier.trim() &&
    (!a.deviceIdentifier.trim() || a.identifierKind === b.identifierKind) &&
    a.text.trim() === b.text.trim() &&
    a.category === b.category &&
    a.status === b.status &&
    a.hasActiveCondition === b.hasActiveCondition &&
    a.conditionType === b.conditionType
  );
}
