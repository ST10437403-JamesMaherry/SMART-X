import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';

/**
 * Provides the permanent Part 1 route for sensor ingestion functionality.
 *
 * Sensor registration, telemetry simulation and secure attachment management
 * are added by their dedicated frontend issues. This page currently
 * establishes the active module route and navigation state.
 */
function IngestionPage() {
  return (
    <ApplicationShell>
      <section className="page-intro module-heading" aria-labelledby="page-title">
        <p className="eyebrow">PART 1 / ACTIVE</p>

        <h1 className="page-title" id="page-title">
          Sensor Data
          <br />
          Ingestion
        </h1>

        <p className="page-description">
          Register sensors, submit telemetry and manage device attachments.
        </p>
      </section>

      <PillarNavigation />
    </ApplicationShell>
  );
}

export default IngestionPage;
