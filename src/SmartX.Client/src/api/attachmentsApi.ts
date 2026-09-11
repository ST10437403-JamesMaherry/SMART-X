import { getBlob, getJson } from './apiClient';

/**
 * Supported Smart-X attachment categories.
 */
export const attachmentCategories = ['Configuration', 'DeploymentPhoto', 'HardwareLog'] as const;

export type AttachmentCategory = (typeof attachmentCategories)[number];

/**
 * Attachment metadata returned by the Smart-X API.
 *
 * The physical encrypted storage path is intentionally not exposed by the
 * backend and therefore does not appear in the browser contract.
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
 * Maximum file size enforced by the backend.
 *
 * Keeping the same limit on the client enables immediate feedback while the
 * backend remains the final security boundary.
 */
export const maxAttachmentSizeBytes = 10 * 1024 * 1024;

/**
 * Retrieves metadata for every attachment belonging to the selected sensor.
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
 * Retrieves and decrypts an attachment through the backend download endpoint.
 *
 * The browser only receives the decrypted response stream. The encryption key
 * and encrypted physical storage remain entirely backend concerns.
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
