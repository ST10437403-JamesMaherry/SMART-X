import { Link } from 'react-router-dom';

import { routes } from '../routing/routes';
import './SelectedSensorLinks.css';

type SourcePillar = 'topology' | 'ingestion' | 'realtime';

interface SelectedSensorLinksProps {
  readonly source: SourcePillar;
  readonly sensorName: string;
}

interface DeviceShortcut {
  readonly to: string;
  readonly label: string;
  readonly pillar: SourcePillar;
}

/**
 * All destinations refer to real, existing sections; URLs carry no sensor
 * identifiers. The application-wide workspace provider owns the selected ID.
 * Navigating never submits telemetry, executes commands, or records a
 * DeviceViewed event. Any deliberate command remains in its owning panel.
 */
const shortcuts: readonly DeviceShortcut[] = [
  {
    to: `${routes.topology}#topology-workspace-title`,
    label: 'Topology routes',
    pillar: 'topology',
  },
  {
    to: `${routes.topology}#topology-history-heading`,
    label: 'Indexed AVL history',
    pillar: 'topology',
  },
  { to: `${routes.ingestion}#sensor-management`, label: 'Sensor registry', pillar: 'ingestion' },
  {
    to: `${routes.ingestion}#telemetry-management`,
    label: 'Submit telemetry',
    pillar: 'ingestion',
  },
  {
    to: `${routes.realtime}#realtime-command-title`,
    label: 'Real-time commands',
    pillar: 'realtime',
  },
  {
    to: `${routes.realtime}#realtime-history-title`,
    label: 'Processed event history',
    pillar: 'realtime',
  },
];

/**
 * Links are rendered only beside an authoritative registered sensor.
 * The navigation is intentionally present in every pillar so an operator can
 * inspect topology, input telemetry, and review command/history for one ID.
 */
export function SelectedSensorLinks({ source, sensorName }: SelectedSensorLinksProps) {
  const destinations = shortcuts.filter((shortcut) => shortcut.pillar !== source);
  // On topology, the AVL panel is farther down the same page. Keep a native
  // hash link to it so the primary historical requirement stays discoverable.
  if (source === 'topology') {
    destinations.unshift(shortcuts[1]);
  }

  return (
    <nav className="device-shortcuts" aria-label="Selected sensor workflows">
      <p className="device-shortcuts__eyebrow">SAME DEVICE / OTHER WORKFLOWS</p>
      <p className="device-shortcuts__context">
        Open another Smart-X pillar for <strong>{sensorName}</strong>. Selection is retained;
        navigating does not submit data or execute a command.
      </p>
      <ul className="device-shortcuts__list">
        {destinations.map(({ to, label }) => (
          <li key={to}>
            <Link className="device-shortcuts__link" to={to}>
              {label} <span aria-hidden="true">↗</span>
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
