import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';

import { getApiHealth } from '../api/healthApi';
import { routes } from '../routing/routes';

/**
 * Represents the current connection state between the React client and the
 * Smart-X API.
 */
type HealthState = 'checking' | 'connected' | 'unavailable';

/**
 * Displays the Smart-X Part 1 gateway landing page.
 *
 * The page deliberately contains only information required to identify the
 * application and communicate backend availability. Additional navigation is
 * introduced separately as part of the three-pillar interface.
 */
function GatewayHomePage() {
  const [healthState, setHealthState] = useState<HealthState>('checking');

  /**
   * Checks API availability asynchronously.
   *
   * An AbortSignal is accepted so that an in-flight request can be safely
   * cancelled when the component unmounts.
   */
  const checkApiHealth = useCallback(async (signal?: AbortSignal) => {
    try {
      await getApiHealth(signal);

      setHealthState('connected');
    } catch (error) {
      /*
       * Request cancellation is a normal lifecycle event and should not be
       * displayed to the operator as a connection failure.
       */
      if (error instanceof DOMException && error.name === 'AbortError') {
        return;
      }

      setHealthState('unavailable');
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const healthCheckTimeout = window.setTimeout(() => {
      void checkApiHealth(controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(healthCheckTimeout);
      controller.abort();
    };
  }, [checkApiHealth]);

  /**
   * Retries the API health request without requiring a page refresh.
   */
  function handleRetry(): void {
    setHealthState('checking');

    void checkApiHealth();
  }

  return (
    <div className="app-shell">
      <div className="page-container">
        <header className="site-header">
          <Link className="wordmark" to={routes.home} aria-label="Smart-X home">
            SMART-X
          </Link>
        </header>

        <main className="page-content">
          <section className="page-intro" aria-labelledby="page-title">
            <p className="eyebrow">PROG7312 / PART 1</p>

            <h1 className="page-title" id="page-title">
              IoT Mesh
              <br />
              Ecosystem
            </h1>

            <p className="page-description">Sensor telemetry management for the Smart-X network.</p>
          </section>

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
        </main>
      </div>
    </div>
  );
}

export default GatewayHomePage;
