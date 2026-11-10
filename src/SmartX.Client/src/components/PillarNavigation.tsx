import { NavLink } from 'react-router-dom';

import { routes } from '../routing/routes';

/**
 * Displays the three permanent architectural pillars of Smart-X.
 *
 * All three architectural pillars are routable in the final PoE.
 * NavLink supplies the active-route accessibility state and preserves normal
 * keyboard, browser-history, and assistive-technology link behaviour.
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
          <NavLink
            to={routes.topology}
            className={({ isActive }) =>
              isActive ? 'pillar-control pillar-control--active' : 'pillar-control'
            }
          >
            {({ isActive }) => (
              <>
                <span className="pillar-title">Network Topology and Mesh Routing</span>

                <span className="pillar-state">{isActive ? 'Active' : 'Open'}</span>
              </>
            )}
          </NavLink>
        </li>
      </ul>
    </nav>
  );
}

export default PillarNavigation;
