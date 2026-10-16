import { getJson } from './apiClient';
import type { RelayCommandType } from './commandsApi';

/**
 * The API is the sole prediction authority. These properties mirror the
 * P2-09 PredictedRecommendationResponse C# contract (camel-cased by ASP.NET).
 * No browser code creates, trains, or ranks prediction patterns.
 */
export interface PredictedRecommendationResponse {
  readonly sourceAlertEpisodeId: string;
  readonly sourceDeviceId: string;
  readonly sourceDeviceName: string;
  readonly conditionType: string;
  readonly severity: string;
  readonly movement: 'NotApplicable' | 'Increase' | 'Decrease';
  readonly targetActuatorId: string;
  readonly targetActuatorName: string;
  readonly commandType: RelayCommandType;
  readonly supportingEpisodes: number;
  readonly lastLearnedAtUtc: string;
  /** Deterministic heuristic points out of 100, never a probability. */
  readonly score: number;
  readonly confidenceBand: 'Emerging' | 'Moderate' | 'High';
  /** Server-computed expiry, including current-condition freshness. */
  readonly expiresAtUtc: string;
  readonly explanation: string;
}

/**
 * One authoritative, read-only evaluation against current devices, active
 * issue episodes, commanded state, and bounded operator-action history.
 */
export interface PredictedRecommendationsResponse {
  readonly evaluatedAtUtc: string;
  readonly minimumSupportingEpisodes: number;
  readonly evidenceWindowSeconds: number;
  readonly nextRefreshAtUtc: string | null;
  readonly recommendations: readonly PredictedRecommendationResponse[];
}

/**
 * A plain GET must not create operator interactions or execute commands.
 * AbortSignal support prevents older responses from replacing newer ones.
 */
export function getPredictedRecommendations(
  signal?: AbortSignal,
): Promise<PredictedRecommendationsResponse> {
  return getJson<PredictedRecommendationsResponse>('/api/realtime/recommendations', signal);
}
