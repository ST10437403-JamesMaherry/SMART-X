import { buildApiUrl, getBlob, getJson } from './apiClient';
import { ApiError, type ApiProblemDetails } from './apiTypes';

/**
 * Supported Smart-X attachment categories.
 */
export const attachmentCategories = ['Configuration', 'DeploymentPhoto', 'HardwareLog'] as const;

export type AttachmentCategory = (typeof attachmentCategories)[number];

/**
 * Attachment metadata returned by the Smart-X API.
 *
 * Physical encrypted storage paths are deliberately not exposed to the
 * browser.
 */
export interface SensorAttachmentResponse {
  readonly id: string;
  readonly sensorProfileId: string;
  readonly fileName: string;
  readonly contentType: string;
  readonly category: AttachmentCategory;
  readonly sizeBytes: number;
  readonly uploadedAtUtc: string;
}

/**
 * Maximum attachment size enforced by the backend.
 */
export const maxAttachmentSizeBytes = 10 * 1024 * 1024;

/**
 * Options used by the progress-aware multipart upload operation.
 */
export interface UploadSensorAttachmentOptions {
  readonly sensorId: string;
  readonly category: AttachmentCategory;
  readonly file: File;
  readonly signal?: AbortSignal;

  /**
   * Reports progress from zero to one hundred where the browser exposes the
   * multipart request length. Null represents an indeterminate upload.
   */
  readonly onProgress?: (progressPercent: number | null) => void;
}

/**
 * Safely attempts to parse ASP.NET ProblemDetails returned from an
 * XMLHttpRequest operation.
 */
function parseProblemDetails(responseText: string): ApiProblemDetails | undefined {
  if (!responseText.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(responseText) as ApiProblemDetails;
  } catch {
    return undefined;
  }
}

/**
 * Uploads one attachment using XMLHttpRequest rather than fetch.
 *
 * Fetch is used for ordinary Smart-X HTTP operations. XMLHttpRequest is used
 * only here because browser upload progress events are required for Issue #17
 * and are not consistently exposed by the standard fetch upload API.
 *
 * The browser sends multipart/form-data directly to the streaming ASP.NET
 * endpoint. The complete file is not manually loaded into JavaScript memory.
 */
export function uploadSensorAttachment({
  sensorId,
  category,
  file,
  signal,
  onProgress,
}: UploadSensorAttachmentOptions): Promise<SensorAttachmentResponse> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('The attachment upload was cancelled.', 'AbortError'));

      return;
    }

    const xhr = new XMLHttpRequest();

    const requestUrl =
      `/api/sensors/${encodeURIComponent(sensorId)}` +
      `/attachments?category=${encodeURIComponent(category)}`;

    let settled = false;

    /**
     * Removes the external abort listener once the request has completed.
     */
    function cleanup(): void {
      signal?.removeEventListener('abort', handleSignalAbort);
    }

    /**
     * Resolves the request only once.
     */
    function resolveOnce(response: SensorAttachmentResponse): void {
      if (settled) {
        return;
      }

      settled = true;
      cleanup();
      resolve(response);
    }

    /**
     * Rejects the request only once.
     */
    function rejectOnce(error: unknown): void {
      if (settled) {
        return;
      }

      settled = true;
      cleanup();
      reject(error);
    }

    /**
     * Cancels the active browser request when the caller's AbortSignal fires.
     */
    function handleSignalAbort(): void {
      xhr.abort();
    }

    xhr.open('POST', buildApiUrl(requestUrl));

    /*
     * Do not set Content-Type manually. The browser must create the multipart
     * boundary used by the ASP.NET MultipartReader.
     */
    xhr.setRequestHeader('Accept', 'application/json');

    xhr.upload.addEventListener('progress', (event: ProgressEvent<EventTarget>) => {
      if (!onProgress) {
        return;
      }

      if (!event.lengthComputable || event.total <= 0) {
        onProgress(null);
        return;
      }

      const percentage = Math.min(100, Math.max(0, Math.round((event.loaded / event.total) * 100)));

      onProgress(percentage);
    });

    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          const response = JSON.parse(xhr.responseText) as SensorAttachmentResponse;

          onProgress?.(100);
          resolveOnce(response);
        } catch {
          rejectOnce(new Error('The Smart-X API returned an unexpected attachment response.'));
        }

        return;
      }

      const problemDetails = parseProblemDetails(xhr.responseText);

      rejectOnce(new ApiError(xhr.status, problemDetails));
    });

    xhr.addEventListener('error', () => {
      rejectOnce(new Error('The Smart-X API could not be reached during the attachment upload.'));
    });

    xhr.addEventListener('abort', () => {
      rejectOnce(new DOMException('The attachment upload was cancelled.', 'AbortError'));
    });

    signal?.addEventListener('abort', handleSignalAbort, {
      once: true,
    });

    const formData = new FormData();

    /*
     * The backend accepts the first multipart form-data section containing a
     * filename. "file" is kept as the conventional field name and matches the
     * project's API examples.
     */
    formData.append('file', file, file.name);

    onProgress?.(0);
    xhr.send(formData);
  });
}

/**
 * Retrieves metadata for every attachment belonging to one registered sensor.
 */
export function getSensorAttachments(
  sensorId: string,
  signal?: AbortSignal,
): Promise<readonly SensorAttachmentResponse[]> {
  return getJson<readonly SensorAttachmentResponse[]>(
    `/api/sensors/${encodeURIComponent(sensorId)}/attachments`,
    signal,
  );
}

/**
 * Retrieves and decrypts one attachment through the backend download
 * endpoint.
 *
 * Encryption keys and encrypted persistent storage remain backend concerns.
 */
export function downloadSensorAttachment(
  sensorId: string,
  attachmentId: string,
  signal?: AbortSignal,
): Promise<Blob> {
  return getBlob(
    `/api/sensors/${encodeURIComponent(sensorId)}/attachments/${encodeURIComponent(attachmentId)}`,
    signal,
  );
}
