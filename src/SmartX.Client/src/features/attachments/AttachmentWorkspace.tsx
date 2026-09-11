import { useCallback, useEffect, useState } from 'react';

import {
  downloadSensorAttachment,
  getSensorAttachments,
  type SensorAttachmentResponse,
} from '../../api/attachmentsApi';
import { getApiErrorMessage } from '../../api/apiTypes';
import type { SensorResponse } from '../../api/sensorsApi';
import AttachmentList from './AttachmentList';
import AttachmentUploader from './AttachmentUploader';
import './AttachmentWorkspace.css';

interface AttachmentWorkspaceProps {
  readonly sensor: SensorResponse | null;
}

/**
 * Owns attachment metadata, upload integration and secure download state for
 * the currently selected Smart-X sensor.
 */
function AttachmentWorkspace({ sensor }: AttachmentWorkspaceProps) {
  const [attachments, setAttachments] = useState<readonly SensorAttachmentResponse[]>([]);

  const [listState, setListState] = useState<'loading' | 'ready' | 'error'>('ready');

  const [listError, setListError] = useState<string | null>(null);

  const [downloadingAttachmentId, setDownloadingAttachmentId] = useState<string | null>(null);

  const [downloadError, setDownloadError] = useState<string | null>(null);

  /**
   * Retrieves attachment metadata for one registered sensor.
   */
  const loadAttachments = useCallback(
    async (sensorId: string, signal?: AbortSignal): Promise<void> => {
      setListState('loading');
      setListError(null);

      try {
        const response = await getSensorAttachments(sensorId, signal);

        setAttachments(response);
        setListState('ready');
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
          return;
        }

        setAttachments([]);
        setListState('error');

        setListError(getApiErrorMessage(error, 'Attachments could not be loaded.'));
      }
    },
    [],
  );

  useEffect(() => {
    if (!sensor) {
      return;
    }

    const controller = new AbortController();

    /*
     * Defer the asynchronous state-producing request beyond the synchronous
     * effect phase to remain compatible with the React hooks lint rules used
     * throughout Smart-X.
     */
    const loadTimer = window.setTimeout(() => {
      void loadAttachments(sensor.id, controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [loadAttachments, sensor]);

  /**
   * Inserts newly uploaded metadata directly into the current list.
   */
  function handleUploaded(attachment: SensorAttachmentResponse): void {
    setAttachments((currentAttachments) => [
      ...currentAttachments.filter((currentAttachment) => currentAttachment.id !== attachment.id),
      attachment,
    ]);

    setListError(null);
    setListState('ready');
  }

  /**
   * Downloads and decrypts one attachment through the Smart-X API.
   *
   * The backend streams decrypted content and the browser receives only the
   * resulting response. The encrypted storage path and encryption key never
   * enter the React application.
   */
  async function handleDownload(attachment: SensorAttachmentResponse): Promise<void> {
    if (!sensor) {
      return;
    }

    setDownloadingAttachmentId(attachment.id);

    setDownloadError(null);

    try {
      const content = await downloadSensorAttachment(sensor.id, attachment.id);

      const downloadUrl = URL.createObjectURL(content);

      const anchor = document.createElement('a');

      anchor.href = downloadUrl;
      anchor.download = attachment.fileName;
      anchor.style.display = 'none';

      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();

      /*
       * Revoke after the browser has received the synthetic click. Delaying
       * until the next task avoids revoking the URL before some browsers begin
       * reading it.
       */
      window.setTimeout(() => {
        URL.revokeObjectURL(downloadUrl);
      }, 0);
    } catch (error) {
      setDownloadError(getApiErrorMessage(error, 'The attachment could not be downloaded.'));
    } finally {
      setDownloadingAttachmentId(null);
    }
  }

  if (!sensor) {
    return (
      <section className="attachment-workspace">
        <header className="attachment-workspace-heading">
          <p className="attachment-workspace-label">ATTACHMENTS</p>

          <h2>Device files</h2>
        </header>

        <p className="attachment-no-sensor">Register or select a sensor to manage attachments.</p>
      </section>
    );
  }

  return (
    <section className="attachment-workspace" aria-labelledby="attachment-workspace-title">
      <header className="attachment-workspace-heading">
        <p className="attachment-workspace-label">ATTACHMENTS</p>

        <h2 id="attachment-workspace-title">Device files</h2>
      </header>

      <div className="attachment-workspace-grid">
        <AttachmentUploader key={sensor.id} sensor={sensor} onUploaded={handleUploaded} />

        <AttachmentList
          sensor={sensor}
          attachments={attachments}
          state={listState}
          errorMessage={listError}
          downloadingAttachmentId={downloadingAttachmentId}
          downloadError={downloadError}
          onRetry={() => {
            void loadAttachments(sensor.id);
          }}
          onDownload={(attachment) => {
            void handleDownload(attachment);
          }}
        />
      </div>
    </section>
  );
}

export default AttachmentWorkspace;
