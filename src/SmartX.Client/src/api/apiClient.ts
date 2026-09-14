import { ApiError, type ApiProblemDetails } from './apiTypes';

const configuredApiBaseUrl = import.meta.env.VITE_API_BASE_URL?.trim();

/**
 * Default API location used when VITE_API_BASE_URL is not explicitly
 * configured.
 *
 * During Vite development the ASP.NET Core HTTPS launch profile remains the
 * default so the normal local workflow is unchanged.
 *
 * Production builds default to an empty base URL. This causes requests such
 * as /api/health and /hubs/dashboard to use the browser's current origin,
 * allowing a reverse proxy to route both React and API traffic through one
 * external address.
 */
const defaultApiBaseUrl = import.meta.env.DEV ? 'https://localhost:7095' : '';

/**
 * Base address of the Smart-X API.
 *
 * VITE_API_BASE_URL remains available for deployments that deliberately host
 * the client and API on different origins. The Docker deployment uses the
 * same-origin production default.
 */
export const apiBaseUrl = (configuredApiBaseUrl || defaultApiBaseUrl).replace(/\/+$/, '');

/**
 * Normalises relative Smart-X API paths and rejects accidental empty
 * requests before they reach the browser networking layer.
 */
function normalizePath(path: string): string {
  const normalizedPath = path.trim();

  if (!normalizedPath) {
    throw new Error('A Smart-X API path is required.');
  }

  return normalizedPath.startsWith('/') ? normalizedPath : `/${normalizedPath}`;
}

/**
 * Creates an absolute API URL from a Smart-X relative route.
 *
 * Exporting this helper allows APIs that require lower-level browser
 * networking primitives, such as XMLHttpRequest upload progress, to use the
 * exact same base URL configuration as ordinary fetch requests.
 */
export function buildApiUrl(path: string): string {
  return `${apiBaseUrl}${normalizePath(path)}`;
}

/**
 * Attempts to read an ASP.NET Core ProblemDetails response.
 *
 * Not every failed HTTP response is guaranteed to contain JSON, so parsing
 * failures are deliberately converted to undefined rather than masking the
 * original HTTP status.
 */
async function readProblemDetails(response: Response): Promise<ApiProblemDetails | undefined> {
  const contentType = response.headers.get('content-type');

  if (!contentType?.includes('json')) {
    return undefined;
  }

  try {
    return (await response.json()) as ApiProblemDetails;
  } catch {
    return undefined;
  }
}

/**
 * Converts an unsuccessful HTTP response into the standard Smart-X client
 * error representation.
 */
export async function createApiError(response: Response): Promise<ApiError> {
  const problemDetails = await readProblemDetails(response);

  return new ApiError(response.status, problemDetails);
}

/**
 * Performs the common Smart-X fetch operation.
 *
 * This method handles base URL resolution and ProblemDetails conversion while
 * leaving response-body handling to the more specialised helper methods.
 */
export async function request(path: string, options: RequestInit = {}): Promise<Response> {
  const headers = new Headers(options.headers);

  if (!headers.has('Accept')) {
    headers.set('Accept', 'application/json');
  }

  const response = await fetch(buildApiUrl(path), {
    ...options,
    headers,
  });

  if (!response.ok) {
    throw await createApiError(response);
  }

  return response;
}

/**
 * Performs a Smart-X API request that expects a JSON response body.
 */
export async function requestJson<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await request(path, options);
  const contentType = response.headers.get('content-type');

  if (!contentType?.includes('application/json')) {
    throw new Error('The Smart-X API returned an unexpected response format.');
  }

  return (await response.json()) as T;
}

/**
 * Performs a typed JSON GET request.
 */
export function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  return requestJson<T>(path, {
    method: 'GET',
    signal,
  });
}

/**
 * Performs a typed JSON POST request.
 */
export function postJson<TResponse, TRequest>(
  path: string,
  body: TRequest,
  signal?: AbortSignal,
): Promise<TResponse> {
  return requestJson<TResponse>(path, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  });
}

/**
 * Performs a typed JSON PUT request.
 */
export function putJson<TResponse, TRequest>(
  path: string,
  body: TRequest,
  signal?: AbortSignal,
): Promise<TResponse> {
  return requestJson<TResponse>(path, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  });
}

/**
 * Retrieves binary content from the Smart-X API.
 *
 * Attachment payloads are decrypted by the backend and streamed directly to
 * this response. The browser therefore receives only the requested plaintext
 * download and never needs access to encryption infrastructure.
 */
export async function getBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const response = await request(path, {
    method: 'GET',
    headers: {
      Accept: '*/*',
    },
    signal,
  });

  return response.blob();
}
