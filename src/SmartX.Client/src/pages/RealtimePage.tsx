import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';
import RealtimeWorkspace from '../features/realtime/RealtimeWorkspace';

/**
 * Activates the genuine Part 2 operations workspace under the existing
 * three-pillar navigation. The shared workspace provider remains mounted
 * across navigation, so selected devices are retained without copying
 * telemetry, command or queue state into client-side storage.
 */
function RealtimePage() {
  return (
    <ApplicationShell>
      <section className="page-intro module-heading" aria-labelledby="page-title">
        <p className="eyebrow">WORKBENCH / LIVE OPERATIONS</p>
        <h1 className="page-title" id="page-title">
          Real-Time
          <br />
          Command Stream
        </h1>
        <p className="page-description">
          Inspect real worker state, device telemetry, actuator state and unique operational
          conditions from the Smart-X API.
        </p>
      </section>

      <PillarNavigation />
      <RealtimeWorkspace />
    </ApplicationShell>
  );
}

export default RealtimePage;
