import { getJson, requestJson } from './apiClient';

/**
 * Operational conditions currently surfaced by the Smart-X Adaptive Anomaly
 * Spotlight.
 *
 * These values mirror DashboardIssueType on the .NET backend.
 */
export type DashboardIssueType =
  | 'TelemetrySpike'
  | 'SensorDisconnected'
  | 'RepeatedFailure'
  | 'ConfigurationIssue'
  | 'SensorStateChange';

/**
 * Severity levels used by the backend priority model.
 */
export type DashboardIssueSeverity = 'Information' | 'Warning' | 'Critical';

/**
 * Operator lifecycle of a dashboard issue.
 *
 * The default active snapshot only contains Active and Acknowledged issues,
 * although Resolved remains part of the transport contract for future history
 * views and explicit includeResolved API requests.
 */
export type DashboardIssueState = 'Active' | 'Acknowledged' | 'Resolved';

/**
 * Smart-X workspace section containing the most useful evidence for an issue.
 */
export type DashboardIssueTarget = 'Sensor' | 'Telemetry' | 'Attachments';

/**
 * One server-authoritative Adaptive Anomaly Spotlight issue.
 */
export interface DashboardIssueResponse {
  readonly id: string;
  readonly sensorProfileId: string;
  readonly sensorName: string;
  readonly type: DashboardIssueType;
  readonly severity: DashboardIssueSeverity;
  readonly state: DashboardIssueState;
  readonly target: DashboardIssueTarget;
  readonly title: string;
  readonly detail: string;
  readonly occurrenceCount: number;
  readonly priorityScore: number;
  readonly firstObservedAtUtc: string;
  readonly lastObservedAtUtc: string;
  readonly acknowledgedAtUtc: string | null;
  readonly resolvedAtUtc: string | null;
}

/**
 * Complete server-authoritative dashboard snapshot.
 */
export interface DashboardSnapshotResponse {
  readonly generatedAtUtc: string;
  readonly activeIssueCount: number;
  readonly unacknowledgedIssueCount: number;
  readonly issues: readonly DashboardIssueResponse[];
}

/**
 * Lightweight invalidation notification delivered through SignalR.
 *
 * The event deliberately does not contain the complete mutable dashboard.
 * React receives this notification and reloads the authoritative HTTP
 * snapshot instead.
 */
export interface DashboardChangedResponse {
  readonly reason: string;
  readonly sensorProfileId: string | null;
  readonly occurredAtUtc: string;
}

/**
 * Retrieves the current prioritized Adaptive Anomaly Spotlight snapshot.
 */
export function getDashboardSnapshot(
  includeResolved = false,
  signal?: AbortSignal,
): Promise<DashboardSnapshotResponse> {
  const path = includeResolved
    ? '/api/dashboard/issues?includeResolved=true'
    : '/api/dashboard/issues';

  return getJson<DashboardSnapshotResponse>(path, signal);
}

/**
 * Acknowledges one active dashboard issue.
 *
 * Acknowledgement records operator awareness but deliberately does not resolve
 * the underlying operational condition.
 */
export function acknowledgeDashboardIssue(
  issueId: string,
  signal?: AbortSignal,
): Promise<DashboardIssueResponse> {
  return requestJson<DashboardIssueResponse>(
    `/api/dashboard/issues/${encodeURIComponent(issueId)}/acknowledge`,
    {
      method: 'POST',
      signal,
    },
  );
}
