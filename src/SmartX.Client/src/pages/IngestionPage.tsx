import { Link } from 'react-router-dom';

import { routes } from '../routing/routes';

/**
 * Provides the Part 1 entry point for Smart-X sensor ingestion functionality.
 *
 * Sensor registration, telemetry simulation and secure attachments will be
 * introduced in their dedicated issues. This route currently establishes the
 * permanent module page within the application's navigation structure.
 */
function IngestionPage() {
  return (
    <div className="app-shell">
      <div className="page-container">
        <header className="site-header">
          <Link className="wordmark" to={routes.home} aria-label="Smart-X home">
            SMART-X
          </Link>
        </header>

        <main className="page-content">
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
        </main>
      </div>
    </div>
  );
}

export default IngestionPage;
