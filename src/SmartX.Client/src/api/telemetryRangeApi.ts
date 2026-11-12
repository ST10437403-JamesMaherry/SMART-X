import { getJson } from './apiClient';
import type { TelemetryDataType } from './sensorsApi';
import type { TelemetryReadingResponse } from './telemetryApi';

/**
 * An AVL keyset cursor is a timestamp AND an insertion-order sequence.
 * Keeping both values prevents skipped or duplicated readings when several
 * accepted packets have the same capture timestamp.
 */
export interface TelemetryRangeCursor {
  readonly capturedAtUtc: string;
  readonly sequence: number;
}

/**
 * Exact response contract from SmartX.Contracts.Telemetry.
 * There is deliberately no total count: obtaining one would require an
 * additional query and the server only promises a bounded current page.
 */
export interface TelemetryRangePageResponse {
  readonly sensorProfileId: string;
  readonly telemetryDataType: TelemetryDataType;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly limit: number;
  readonly readings: readonly TelemetryReadingResponse[];
  readonly hasMore: boolean;
  readonly nextCursor: TelemetryRangeCursor | null;
}

/**
 * Immutable, inclusive query bounds. The server accepts 1..1000 readings;
 * this UI intentionally offers smaller pages to keep browser memory bounded.
 */
export interface TelemetryRangeQuery {
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly limit: number;
}

export const TOPOLOGY_HISTORY_PAGE_SIZES = [2, 25, 50, 100] as const;

/**
 * Converts the native datetime-local control's local wall clock value into
 * an explicit UTC instant. Never send a timezone-less timestamp to the API.
 */
export function localRangeToUtc(
  fromLocal: string,
  toLocal: string,
): Pick<TelemetryRangeQuery, 'fromUtc' | 'toUtc'> {
  if (!fromLocal || !toLocal) {
    throw new RangeError('Choose both the range start and range end.');
  }

  const from = new Date(fromLocal);
  const to = new Date(toLocal);

  if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime())) {
    throw new RangeError('Enter valid local date and time values.');
  }

  if (from.getTime() > to.getTime()) {
    throw new RangeError('Range end must be on or after range start.');
  }

  return { fromUtc: from.toISOString(), toUtc: to.toISOString() };
}

/**
 * Reads a single page from the EXISTING server AVL index. No recent-history
 * fetch and no client-side sorting, filtering, or full-series download.
 */
export function getIndexedTelemetryPage(
  sensorProfileId: string,
  query: TelemetryRangeQuery,
  after: TelemetryRangeCursor | null = null,
  signal?: AbortSignal,
): Promise<TelemetryRangePageResponse> {
  if (!sensorProfileId.trim()) {
    throw new RangeError('A registered sensor must be selected.');
  }

  if (!Number.isInteger(query.limit) || query.limit < 1 || query.limit > 100) {
    throw new RangeError('The dashboard page size must be between 1 and 100.');
  }

  const params = new URLSearchParams({
    fromUtc: query.fromUtc,
    toUtc: query.toUtc,
    limit: String(query.limit),
  });

  if (after !== null) {
    params.set('afterUtc', after.capturedAtUtc);
    params.set('afterSequence', String(after.sequence));
  }

  return getJson<TelemetryRangePageResponse>(
    `/api/sensors/${encodeURIComponent(sensorProfileId)}/telemetry/range?${params.toString()}`,
    signal,
  );
}
