const configuredApiBaseUrl = import.meta.env.VITE_API_BASE_URL?.trim();

/**
 * The API base URL can be overridden through Vite environment configuration.
 */
export const apiBaseUrl = (configuredApiBaseUrl || 'https://localhost:7095').replace(/\/+$/, '');

/**
 * Performs a typed JSON GET request against the Smart-X API.
 */
export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;

  const response = await fetch(`${apiBaseUrl}${normalizedPath}`, {
    method: 'GET',
    headers: {
      Accept: 'application/json',
    },
    signal,
  });

  if (!response.ok) {
    throw new Error(`API request failed with status ${response.status}.`);
  }

  const contentType = response.headers.get('content-type');

  if (!contentType?.includes('application/json')) {
    throw new Error('The API returned an unexpected response format.');
  }

  return (await response.json()) as T;
}
