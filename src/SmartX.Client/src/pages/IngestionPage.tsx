import ApplicationShell from '../components/ApplicationShell';
import PillarNavigation from '../components/PillarNavigation';
import SensorWorkspace from '../features/sensors/SensorWorkspace';

/**
 * Hosts the active Part 1 Sensor Data Ingestion and Telemetry module.
 *
 * The page preserves the permanent Smart-X architectural navigation while the
 * ingestion workspace progressively gains sensor registration, telemetry and
 * attachment functionality.
 */
function IngestionPage() {
  return (
    <ApplicationShell>
      <section className="page-intro module-heading" aria-labelledby="page-title">
        <p className="eyebrow">WORKBENCH / SENSOR INGESTION</p>

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

      <SensorWorkspace />
    </ApplicationShell>
  );
}

export default IngestionPage;
