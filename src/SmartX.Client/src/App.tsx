import { useCallback, useEffect, useState } from 'react';
import { apiBaseUrl } from './api/apiClient';
import { getApiHealth, type HealthResponse } from './api/healthApi';
import './App.css';

type HealthState =
  { kind: 'checking' } | { kind: 'connected'; response: HealthResponse } | { kind: 'unavailable' };

function formatCheckedTime(timestamp: string): string {
  const parsedTimestamp = new Date(timestamp);

  if (Number.isNaN(parsedTimestamp.getTime())) {
    return 'Recently';
  }

  return new Intl.DateTimeFormat('en-ZA', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(parsedTimestamp);
}

function App() {
  const [healthState, setHealthState] = useState<HealthState>({
    kind: 'checking',
  });

  const checkApiHealth = useCallback(async (signal?: AbortSignal) => {
    try {
      const response = await getApiHealth(signal);
      setHealthState({ kind: 'connected', response });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        return;
      }

      setHealthState({ kind: 'unavailable' });
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const healthCheckTimer = window.setTimeout(() => {
      void checkApiHealth(controller.signal);
    }, 0);

    return () => {
      window.clearTimeout(healthCheckTimer);
      controller.abort();
    };
  }, [checkApiHealth]);

  function handleRetry(): void {
    setHealthState({ kind: 'checking' });
    void checkApiHealth();
  }

  return (
    <div className="app-shell">
      <div className="ambient-glow" aria-hidden="true" />

      <main className="baseline-card">
        <header className="brand">
          <span className="brand-mark" aria-hidden="true">
            <span />
          </span>
          <span>Smart-X</span>
        </header>

        <section className="intro" aria-labelledby="page-title">
          <p className="eyebrow">PROG7312 · PART 1</p>
          <h1 id="page-title">IoT Mesh Ecosystem</h1>
          <p className="intro-copy">
            A secure full-stack foundation for connected sensor telemetry.
          </p>
        </section>

        <section
          className={`status-card status-card--${healthState.kind}`}
          aria-live="polite"
          aria-atomic="true"
        >
          <div className="status-heading">
            <span
              className={`status-indicator status-indicator--${healthState.kind}`}
              aria-hidden="true"
            />

            <div>
              {healthState.kind === 'checking' && (
                <>
                  <p className="status-label">Checking connection</p>
                  <h2>Contacting the Smart-X API</h2>
                  <p className="status-description">Requesting {apiBaseUrl}/api/health</p>
                </>
              )}

              {healthState.kind === 'connected' && (
                <>
                  <p className="status-label">System available</p>
                  <h2>Client and API connected</h2>
                  <p className="status-description">
                    The full-stack development baseline is operational.
                  </p>
                </>
              )}

              {healthState.kind === 'unavailable' && (
                <>
                  <p className="status-label">Connection unavailable</p>
                  <h2>The API could not be reached</h2>
                  <p className="status-description">
                    Start the HTTPS API profile and try the connection again.
                  </p>
                </>
              )}
            </div>
          </div>

          {healthState.kind === 'connected' && (
            <dl className="health-details">
              <div>
                <dt>Service</dt>
                <dd>{healthState.response.service}</dd>
              </div>
              <div>
                <dt>Status</dt>
                <dd>{healthState.response.status}</dd>
              </div>
              <div>
                <dt>Checked</dt>
                <dd>{formatCheckedTime(healthState.response.checkedAtUtc)}</dd>
              </div>
            </dl>
          )}

          {healthState.kind === 'unavailable' && (
            <button className="retry-button" type="button" onClick={handleRetry}>
              Check again
            </button>
          )}
        </section>

        <footer className="technology-list" aria-label="Application technologies">
          <span>React + TypeScript</span>
          <span>.NET 10 Web API</span>
          <span>Automated CI</span>
        </footer>
      </main>
    </div>
  );
}

export default App;
