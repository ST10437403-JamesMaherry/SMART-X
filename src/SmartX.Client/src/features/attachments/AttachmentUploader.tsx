import {
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
  type FormEvent,
} from 'react';

import {
  attachmentCategories,
  uploadSensorAttachment,
  type AttachmentCategory,
  type SensorAttachmentResponse,
} from '../../api/attachmentsApi';
import { getApiErrorMessage } from '../../api/apiTypes';
import type { SensorResponse } from '../../api/sensorsApi';
import {
  formatAttachmentCategory,
  getAttachmentAccept,
  getAttachmentGuidance,
  prepareAttachmentFile,
  validateAttachmentFile,
} from './attachmentValidation';

interface AttachmentUploaderProps {
  readonly sensor: SensorResponse;
  readonly onUploaded: (attachment: SensorAttachmentResponse) => void;
}

type UploadState =
  | { kind: 'idle' }
  | {
      kind: 'uploading';
      progressPercent: number | null;
    }
  | { kind: 'success'; message: string }
  | { kind: 'cancelled'; message: string }
  | { kind: 'error'; message: string };

/**
 * Formats file sizes using binary units consistent with the backend's MiB
 * attachment limit.
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
 * Provides file-picker and drag-and-drop attachment selection for the
 * currently selected Smart-X sensor.
 */
function AttachmentUploader({ sensor, onUploaded }: AttachmentUploaderProps) {
  const [category, setCategory] = useState<AttachmentCategory>('Configuration');

  const [selectedFile, setSelectedFile] = useState<File | null>(null);

  const [isDragging, setIsDragging] = useState(false);

  const [uploadState, setUploadState] = useState<UploadState>({
    kind: 'idle',
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

  const uploadControllerRef = useRef<AbortController | null>(null);

  const fileValidationError =
    selectedFile === null ? null : validateAttachmentFile(selectedFile, category);

  /**
   * Abort an in-progress upload if this sensor-specific uploader unmounts.
   */
  useEffect(() => {
    return () => {
      uploadControllerRef.current?.abort();
    };
  }, []);

  /**
   * Applies one newly selected file and clears feedback from a previous
   * operation.
   */
  function chooseFile(file: File | null): void {
    setSelectedFile(file);

    setUploadState({
      kind: 'idle',
    });
  }

  /**
   * Handles ordinary file-picker selection.
   */
  function handleFileInputChange(event: ChangeEvent<HTMLInputElement>): void {
    chooseFile(event.target.files?.[0] ?? null);
  }

  /**
   * Accepts the first file dropped into the attachment target.
   */
  function handleDrop(event: DragEvent<HTMLDivElement>): void {
    event.preventDefault();

    setIsDragging(false);

    const droppedFile = event.dataTransfer.files?.[0] ?? null;

    chooseFile(droppedFile);
  }

  /**
   * Cancels the current browser upload request.
   */
  function cancelUpload(): void {
    uploadControllerRef.current?.abort();
  }

  /**
   * Validates and uploads the selected file.
   */
  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();

    if (!selectedFile) {
      setUploadState({
        kind: 'error',
        message: 'Select an attachment before uploading.',
      });

      fileInputRef.current?.focus();
      return;
    }

    const validationError = validateAttachmentFile(selectedFile, category);

    if (validationError) {
      setUploadState({
        kind: 'error',
        message: validationError,
      });

      fileInputRef.current?.focus();
      return;
    }

    const controller = new AbortController();

    uploadControllerRef.current = controller;

    setUploadState({
      kind: 'uploading',
      progressPercent: 0,
    });

    try {
      const preparedFile = prepareAttachmentFile(selectedFile);

      const attachment = await uploadSensorAttachment({
        sensorId: sensor.id,
        category,
        file: preparedFile,
        signal: controller.signal,
        onProgress: (progressPercent) => {
          setUploadState({
            kind: 'uploading',
            progressPercent,
          });
        },
      });

      onUploaded(attachment);

      setSelectedFile(null);

      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }

      setUploadState({
        kind: 'success',
        message: `${attachment.fileName} uploaded.`,
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        setUploadState({
          kind: 'cancelled',
          message: 'Upload cancelled.',
        });

        return;
      }

      setUploadState({
        kind: 'error',
        message: getApiErrorMessage(error, 'The attachment could not be uploaded.'),
      });
    } finally {
      uploadControllerRef.current = null;
    }
  }

  const isUploading = uploadState.kind === 'uploading';

  return (
    <section className="attachment-panel" aria-labelledby="attachment-upload-title">
      <header className="attachment-panel-heading">
        <h3 id="attachment-upload-title">Upload attachment</h3>
      </header>

      <div className="attachment-sensor-context">
        <strong>{sensor.name}</strong>
        <span>{sensor.deviceIdentifier}</span>
      </div>

      <form
        className="attachment-upload-form"
        noValidate
        onSubmit={(event) => {
          void handleSubmit(event);
        }}
      >
        <div className="attachment-field">
          <label htmlFor="attachment-category">Category</label>

          <select
            id="attachment-category"
            value={category}
            disabled={isUploading}
            onChange={(event) => {
              setCategory(event.target.value as AttachmentCategory);

              setUploadState({
                kind: 'idle',
              });
            }}
          >
            {attachmentCategories.map((attachmentCategory) => (
              <option key={attachmentCategory} value={attachmentCategory}>
                {formatAttachmentCategory(attachmentCategory)}
              </option>
            ))}
          </select>

          <p className="attachment-guidance">{getAttachmentGuidance(category)}</p>
        </div>

        <div
          className={
            isDragging
              ? 'attachment-drop-zone attachment-drop-zone--dragging'
              : 'attachment-drop-zone'
          }
          onDragEnter={(event) => {
            event.preventDefault();

            if (!isUploading) {
              setIsDragging(true);
            }
          }}
          onDragOver={(event) => {
            event.preventDefault();
          }}
          onDragLeave={(event) => {
            event.preventDefault();
            setIsDragging(false);
          }}
          onDrop={(event) => {
            if (isUploading) {
              event.preventDefault();
              return;
            }

            handleDrop(event);
          }}
        >
          <input
            ref={fileInputRef}
            className="attachment-file-input"
            id="attachment-file"
            name="attachmentFile"
            type="file"
            accept={getAttachmentAccept(category)}
            disabled={isUploading}
            onChange={handleFileInputChange}
          />

          <label className="attachment-file-picker" htmlFor="attachment-file">
            Choose file
          </label>

          <span className="attachment-drop-copy">or drop it here</span>
        </div>

        {selectedFile && (
          <div className="attachment-selection">
            <span>{selectedFile.name}</span>
            <span>{formatFileSize(selectedFile.size)}</span>
          </div>
        )}

        {fileValidationError && (
          <p className="attachment-feedback attachment-feedback--error" role="alert">
            {fileValidationError}
          </p>
        )}

        {uploadState.kind === 'uploading' && (
          <div
            className="attachment-progress"
            role="progressbar"
            aria-label="Attachment upload progress"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={uploadState.progressPercent ?? undefined}
            aria-valuetext={
              uploadState.progressPercent === null
                ? 'Uploading'
                : `${uploadState.progressPercent}% uploaded`
            }
          >
            <div className="attachment-progress-heading">
              <span>Uploading</span>

              <span>
                {uploadState.progressPercent === null
                  ? 'In progress'
                  : `${uploadState.progressPercent}%`}
              </span>
            </div>

            <div className="attachment-progress-track" aria-hidden="true">
              <span
                style={{
                  width:
                    uploadState.progressPercent === null
                      ? '100%'
                      : `${uploadState.progressPercent}%`,
                }}
              />
            </div>
          </div>
        )}

        <div className="attachment-actions">
          <button
            className="attachment-primary-action"
            type="submit"
            disabled={isUploading || Boolean(fileValidationError)}
          >
            {isUploading ? 'Uploading...' : 'Upload file'}
          </button>

          {isUploading && (
            <button className="attachment-secondary-action" type="button" onClick={cancelUpload}>
              Cancel
            </button>
          )}
        </div>

        {uploadState.kind === 'success' && (
          <p className="attachment-feedback attachment-feedback--success" role="status">
            {uploadState.message}
          </p>
        )}

        {uploadState.kind === 'cancelled' && (
          <p className="attachment-feedback" role="status">
            {uploadState.message}
          </p>
        )}

        {uploadState.kind === 'error' && (
          <p className="attachment-feedback attachment-feedback--error" role="alert">
            {uploadState.message}
          </p>
        )}
      </form>
    </section>
  );
}

export default AttachmentUploader;
