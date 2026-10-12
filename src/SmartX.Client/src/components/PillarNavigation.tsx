import { NavLink } from 'react-router-dom';

import { routes } from '../routing/routes';

/**
 * Displays the three permanent architectural pillars of Smart-X.
 *
 * Part 1 ingestion and the Part 2 command workspace are routable. Topology
 * remains visible, keyboard discoverable and semantically unavailable until
 * the final PoE. NavLink supplies the active-route accessibility state.
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
          <NavLink
            to={routes.realtime}
            className={({ isActive }) =>
              isActive ? 'pillar-control pillar-control--active' : 'pillar-control'
            }
          >
            {({ isActive }) => (
              <>
                <span className="pillar-title">Real-Time Command Stream and History</span>

                <span className="pillar-state">{isActive ? 'Active' : 'Open'}</span>
              </>
            )}
          </NavLink>
        </li>

        <li className="pillar-item">
          <button
            className="pillar-control pillar-control--disabled"
            type="button"
            aria-disabled="true"
            aria-describedby="topology-module-description"
          >
            <span className="pillar-title">Network Topology and Mesh Routing</span>

            <span className="pillar-state">Final PoE</span>
          </button>

          {/*
           * The future availability explanation remains accessible without
           * adding unnecessary visible copy to the interface.
           */}
          <span id="topology-module-description" className="visually-hidden">
            This module is unavailable in Part 2 and will be implemented in the final PoE.
          </span>
        </li>
      </ul>
    </nav>
  );
}

export default PillarNavigation;
