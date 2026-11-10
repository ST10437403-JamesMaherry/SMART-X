/**
 * Central route definitions for the Smart-X client.
 *
 * Keeping route paths in one location prevents components from scattering
 * hard-coded URL strings throughout the application
 */
export const routes = {
  home: '/',
  ingestion: '/ingestion',
  realtime: '/realtime',
  topology: '/topology',
} as const;
