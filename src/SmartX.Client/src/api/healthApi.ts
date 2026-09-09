import { getJson } from './apiClient';

export interface HealthResponse {
  readonly status: string;
  readonly service: string;
  readonly checkedAtUtc: string;
}

/**
 * Retrieves the current availability of the Smart-X API.
 */
export function getApiHealth(signal?: AbortSignal): Promise<HealthResponse> {
  return getJson<HealthResponse>('/api/health', signal);
}
