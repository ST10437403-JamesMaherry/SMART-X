import { NavLink } from 'react-router-dom';

import { routes } from '../routing/routes';

/**
 * Displays the three permanent architectural pillars of Smart-X.
 *
 * Only the Part 1 ingestion module is currently routable. The Part 2 and
 * final PoE modules remain visible so that the application architecture is
 * clear, but their controls are intentionally disabled until those features
 * are implemented.
 */
function PillarNavigation() {
  return (
    <nav className="pillar-navigation" aria-label="Smart-X architectural modules">
      <h2 className="visually-hidden">Smart-X modules</h2>

      <ul className="pillar-list">
        <li className="pillar-item">
          <NavLink
            to={routes.ingestion}
            className={({ isActive }) =>
              isActive ? 'pillar-control pillar-control--active' : 'pillar-control'
            }
          >
            {({ isActive }) => (
              <>
                <span className="pillar-title">Sensor Data Ingestion and Telemetry</span>

                <span className="pillar-state">{isActive ? 'Active' : 'Open'}</span>
              </>
            )}
          </NavLink>
        </li>

        <li className="pillar-item">
          <button
            className="pillar-control pillar-control--disabled"
            type="button"
            disabled
            aria-label="Real-Time Command Stream and History. Unavailable until Part 2."
          >
            <span className="pillar-title">Real-Time Command Stream and History</span>

            <span className="pillar-state">Part 2</span>
          </button>
        </li>

        <li className="pillar-item">
          <button
            className="pillar-control pillar-control--disabled"
            type="button"
            disabled
            aria-label="Network Topology and Mesh Routing. Unavailable until the final PoE."
          >
            <span className="pillar-title">Network Topology and Mesh Routing</span>

            <span className="pillar-state">Final PoE</span>
          </button>
        </li>
      </ul>
    </nav>
  );
}

export default PillarNavigation;
