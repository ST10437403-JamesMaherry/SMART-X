import { useCallback, useEffect, useRef, useState } from 'react';

import { getApiHealth } from '../api/healthApi';
import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';

/**
 * The future mesh API is not yet available in P3-01. We only probe the
 * existing health endpoint, so the page never invents network nodes or routes.
 */
type GatewayHealthState = 'checking' | 'connected' | 'unavailable';

/**
 * Hosts Smart-X's third architectural pillar while the graph services are
 * introduced in P3-04 through P3-08.
 *
 * The navigation is fully usable now, but network topology data is explicitly
 * unavailable until the real backend APIs exist. This preserves honest UI
 * semantics and avoids suggesting that an empty graph means zero devices.
 */
function TopologyPage() {
  const [healthState, setHealthState] = useState<GatewayHealthState>('checking');
  const healthControllerRef = useRef<AbortController | null>(null);

  /**
   * Check the real gateway health endpoint. Cancel an older request before
   * starting another so a slow response cannot overwrite the latest result.
   */
  const checkGatewayHealth = useCallback(async (): Promise<void> => {
    healthControllerRef.current?.abort();

    const controller = new AbortController();
    healthControllerRef.current = controller;

    try {
      await getApiHealth(controller.signal);

      if (!controller.signal.aborted) {
        setHealthState('connected');
      }
    } catch {
      // Aborted requests are expected during navigation and explicit retries.
      if (!controller.signal.aborted) {
        setHealthState('unavailable');
      }
    } finally {
      if (healthControllerRef.current === controller) {
        healthControllerRef.current = null;
      }
    }
  }, []);

  useEffect(() => {
    // Keep the external API effect asynchronous for React's hooks lint rules.
    const checkTimer = window.setTimeout(() => {
      void checkGatewayHealth();
    }, 0);

    return () => {
      window.clearTimeout(checkTimer);
      healthControllerRef.current?.abort();
    };
  }, [checkGatewayHealth]);

  /**
   * Allow an operator to retry the health probe without losing route state.
   */
  function handleRetry(): void {
    setHealthState('checking');
    void checkGatewayHealth();
  }

  return (
    <ApplicationShell>
      <section className="page-intro module-heading" aria-labelledby="page-title">
        <p className="eyebrow">PART 3 / WORKSPACE</p>

        <h1 className="page-title" id="page-title">
          Network Topology
          <br />
          &amp; Mesh Routing
        </h1>

        <p className="page-description">
          Inspect gateway connectivity, trace device paths and evaluate mesh routing when the
          network model is connected.
        </p>
      </section>

      <PillarNavigation />

      <section
        className={`system-status system-status--${healthState}`}
        aria-labelledby="topology-status-heading"
        aria-live="polite"
        aria-atomic="true"
      >
        {healthState === 'checking' && (
          <>
            <p className="status-label">Gateway status</p>
            <h2 className="status-title" id="topology-status-heading">
              Checking connection
            </h2>
            <p className="status-message">
              Checking the Smart-X API before loading the mesh workspace.
            </p>
          </>
        )}

        {healthState === 'connected' && (
          <>
            <p className="status-label">API connected / Topology pending</p>
            <h2 className="status-title" id="topology-status-heading">
              Mesh data is not available yet
            </h2>
            <p className="status-message">
              The gateway is reachable. Device links, network traversal and minimum-spanning-tree
              routes will appear here when the real topology API is implemented. No simulated
              network graph is displayed before those services exist.
            </p>
          </>
        )}

        {healthState === 'unavailable' && (
          <>
            <p className="status-label">Gateway unavailable</p>
            <h2 className="status-title" id="topology-status-heading">
              The Smart-X API could not be reached
            </h2>
            <p className="status-message">
              Start the API and check again. Network topology data cannot be loaded while the
              gateway is disconnected.
            </p>
            <button className="text-button" type="button" onClick={handleRetry}>
              Check again
            </button>
          </>
        )}
      </section>
    </ApplicationShell>
  );
}

export default TopologyPage;
