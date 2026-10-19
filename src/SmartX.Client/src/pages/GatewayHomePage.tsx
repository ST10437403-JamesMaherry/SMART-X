import { useCallback, useEffect, useState } from 'react';

import { getApiHealth } from '../api/healthApi';
import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';

/**
 * Represents the current connection state between the React client and the
 * Smart-X API.
 */
type HealthState = 'checking' | 'connected' | 'unavailable';

/**
 * Displays the Smart-X gateway landing page.
 *
 * The page presents the permanent architectural navigation alongside a
 * lightweight API availability indicator. Only information required for
 * navigation or operational feedback is displayed.
 */
function GatewayHomePage() {
  const [healthState, setHealthState] = useState<HealthState>('checking');

  /**
   * Checks API availability asynchronously.
   *
   * An AbortSignal allows an in-flight request to be cancelled safely when
   * the page unmounts.
   */
  const checkApiHealth = useCallback(async (signal?: AbortSignal) => {
    try {
      await getApiHealth(signal);

      setHealthState('connected');
    } catch (error) {
      /*
       * Request cancellation is expected when React unmounts the page and
       * should never be displayed as an API failure.
       */
      if (error instanceof DOMException && error.name === 'AbortError') {
        return;
      }

      setHealthState('unavailable');
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();

    /*
     * Defer the initial health check until after the synchronous effect phase.
     *
     * This keeps the effect focused on synchronising with the external API
     * while avoiding synchronous state-update chains flagged by React's hooks
     * lint rules.
     */
    const healthCheckTimer = window.setTimeout(() => {
      void checkApiHealth(controller.signal);
    }, 0);

    return () => {
      /*
       * Clear a health check that has not yet started and cancel an active
       * request if the page unmounts while the request is in progress.
       */
      window.clearTimeout(healthCheckTimer);
      controller.abort();
    };
  }, [checkApiHealth]);

  /**
   * Retries the API health request without requiring a full browser refresh.
   */
  function handleRetry(): void {
    setHealthState('checking');

    void checkApiHealth();
  }

  return (
    <ApplicationShell>
      <section className="page-intro" aria-labelledby="page-title">
        <p className="eyebrow">PROG7312 / PART 2</p>

        <h1 className="page-title" id="page-title">
          IoT Mesh
          <br />
          Ecosystem
        </h1>

        <p className="page-description">Sensor telemetry management for the Smart-X network.</p>
      </section>

      <PillarNavigation />

      <section
        className={`system-status system-status--${healthState}`}
        aria-live="polite"
        aria-atomic="true"
      >
        {healthState === 'checking' && (
          <>
            <p className="status-label">API status</p>
            <h2 className="status-title">Checking connection</h2>
          </>
        )}

        {healthState === 'connected' && (
          <>
            <p className="status-label">API connected</p>
            <h2 className="status-title">Gateway available</h2>
          </>
        )}

        {healthState === 'unavailable' && (
          <>
            <p className="status-label">Connection unavailable</p>

            <h2 className="status-title">The API could not be reached</h2>

            <p className="status-message">Start the Smart-X API and try again.</p>

            <button className="text-button" type="button" onClick={handleRetry}>
              Check again
            </button>
          </>
        )}
      </section>
    </ApplicationShell>
  );
}

export default GatewayHomePage;
