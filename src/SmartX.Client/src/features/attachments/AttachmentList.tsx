import type { SensorAttachmentResponse } from '../../api/attachmentsApi';
import type { SensorResponse } from '../../api/sensorsApi';
import { formatAttachmentCategory } from './attachmentValidation';

interface AttachmentListProps {
  readonly sensor: SensorResponse;
  readonly attachments: readonly SensorAttachmentResponse[];
  readonly state: 'loading' | 'ready' | 'error';
  readonly errorMessage: string | null;
  readonly downloadingAttachmentId: string | null;
  readonly downloadError: string | null;
  readonly onRetry: () => void;
  readonly onDownload: (attachment: SensorAttachmentResponse) => void;
}

/**
 * Formats stored attachment sizes using binary units.
 */
function formatFileSize(sizeBytes: number): string {
  if (sizeBytes < 1024) {
    return `${sizeBytes} B`;
  }

  if (sizeBytes < 1024 * 1024) {
    return `${(sizeBytes / 1024).toFixed(1)} KiB`;
  }

  return `${(sizeBytes / (1024 * 1024)).toFixed(2)} MiB`;
}

/**
 * Formats the backend UTC upload timestamp for concise local display.
 */
function formatUploadedAt(timestamp: string): string {
  const parsedTimestamp = new Date(timestamp);

  if (Number.isNaN(parsedTimestamp.getTime())) {
    return timestamp;
  }

  return new Intl.DateTimeFormat('en-ZA', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(parsedTimestamp);
}

/**
 * Displays attachment metadata for the selected Smart-X sensor and exposes
 * secure download actions through the API.
 */
function AttachmentList({
  sensor,
  attachments,
  state,
  errorMessage,
  downloadingAttachmentId,
  downloadError,
  onRetry,
  onDownload,
}: AttachmentListProps) {
  return (
    <section
      className="attachment-panel attachment-list-panel"
      aria-labelledby="attachment-list-title"
    >
      <header className="attachment-panel-heading">
        <h3 id="attachment-list-title">Attachments</h3>

        <span className="attachment-count" aria-label={`${attachments.length} attachments`}>
          {String(attachments.length).padStart(2, '0')}
        </span>
      </header>

      {state === 'loading' && (
        <p className="attachment-empty-state" role="status">
          Loading attachments...
        </p>
      )}

      {state === 'error' && (
        <div className="attachment-empty-state" role="alert">
          <p>{errorMessage ?? 'Attachments could not be loaded.'}</p>

          <button className="attachment-secondary-action" type="button" onClick={onRetry}>
            Try again
          </button>
        </div>
      )}

      {state === 'ready' && attachments.length === 0 && (
        <p className="attachment-empty-state">No attachments for {sensor.name}.</p>
      )}

      {state === 'ready' && attachments.length > 0 && (
        <ul className="attachment-list">
          {attachments.map((attachment) => {
            const isDownloading = downloadingAttachmentId === attachment.id;

            return (
              <li key={attachment.id}>
                <div className="attachment-row">
                  <div className="attachment-row-primary">
                    <strong>{attachment.fileName}</strong>

                    <span>{formatAttachmentCategory(attachment.category)}</span>
                  </div>

                  <div className="attachment-row-meta">
                    <span>{formatFileSize(attachment.sizeBytes)}</span>

                    <time dateTime={attachment.uploadedAtUtc}>
                      {formatUploadedAt(attachment.uploadedAtUtc)}
                    </time>
                  </div>

                  <button
                    className="attachment-download-action"
                    type="button"
                    disabled={downloadingAttachmentId !== null}
                    onClick={() => {
                      onDownload(attachment);
                    }}
                  >
                    {isDownloading ? 'Downloading...' : 'Download'}
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {downloadError && (
        <p className="attachment-feedback attachment-feedback--error" role="alert">
          {downloadError}
        </p>
      )}
    </section>
  );
}

export default AttachmentList;
