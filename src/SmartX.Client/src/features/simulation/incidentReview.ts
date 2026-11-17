import type { DashboardIssueResponse } from '../../api/dashboardApi';
import type { TelemetryTraceResult } from './telemetryTrace';

/**
 * Incident review must never cross a sensor boundary or acknowledge an
 * unrelated issue. Classification and issue membership remain server-owned.
 */
export function isTraceIssue(
  issue: DashboardIssueResponse,
  trace: TelemetryTraceResult,
): boolean {
  return (
    issue.sensorProfileId.toLowerCase() === trace.accepted.sensorProfileId.toLowerCase() &&
    (issue.type === 'TelemetrySpike' || issue.type === 'RepeatedFailure') &&
    issue.state !== 'Resolved'
  );
}

/**
 * Revalidate the exact episode before mutation. A new issue ID indicates a
 * fresh occurrence and must never inherit approval for an older episode.
 */
export function isSameActiveEpisode(
  reviewed: DashboardIssueResponse,
  current: DashboardIssueResponse,
): boolean {
  return (
    reviewed.id.toLowerCase() === current.id.toLowerCase() &&
    reviewed.sensorProfileId.toLowerCase() === current.sensorProfileId.toLowerCase() &&
    reviewed.type === current.type &&
    current.state === 'Active'
  );
}
