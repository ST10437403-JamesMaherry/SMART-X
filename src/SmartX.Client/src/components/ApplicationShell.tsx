import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

import { routes } from '../routing/routes';

interface ApplicationShellProps {
  readonly children: ReactNode;
}

/**
 * Provides the shared structural shell used by Smart-X pages.
 *
 * Keeping the application header and page container in one component prevents
 * individual feature pages from duplicating layout markup as the Part 1,
 * Part 2 and final PoE interfaces grow.
 */
function ApplicationShell({ children }: ApplicationShellProps) {
  return (
    <div className="app-shell">
      <div className="page-container">
        <header className="site-header">
          <Link className="wordmark" to={routes.home} aria-label="Smart-X home">
            SMART-X
          </Link>
        </header>

        <main className="page-content">{children}</main>
      </div>
    </div>
  );
}

export default ApplicationShell;
