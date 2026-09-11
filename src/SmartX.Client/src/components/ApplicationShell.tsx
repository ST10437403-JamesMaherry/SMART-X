import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

import { routes } from '../routing/routes';

interface ApplicationShellProps {
  readonly children: ReactNode;
}

/**
 * Provides the permanent structural shell used across Smart-X.
 *
 * Centralising the header and main content region prevents duplicated layout
 * markup and ensures that accessibility behaviour remains consistent as new
 * PoE modules are introduced.
 */
function ApplicationShell({ children }: ApplicationShellProps) {
  return (
    <div className="app-shell">
      {/*
       * The skip link is visually hidden until focused. This allows keyboard
       * and assistive-technology users to bypass repeated navigation without
       * adding visual clutter to the normal interface.
       */}
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>

      <div className="page-container">
        <header className="site-header">
          <Link className="wordmark" to={routes.home} aria-label="Smart-X home">
            SMART-X
          </Link>
        </header>

        <main id="main-content" className="page-content">
          {children}
        </main>
      </div>
    </div>
  );
}

export default ApplicationShell;
