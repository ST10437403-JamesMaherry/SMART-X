import { useEffect } from 'react';
import { useLocation } from 'react-router-dom';

/**
 * Target IDs are fixed application sections, not arbitrary query parameters.
 * Ingestion and Real-Time mount their destination panels only after HTTP
 * confirms registered devices. Native hash scrolling alone can run too soon.
 */
const allowedTargets = new Set([
  'sensor-management',
  'telemetry-management',
  'realtime-device-title',
  'realtime-command-title',
  'realtime-history-title',
  'topology-workspace-title',
  'topology-history-heading',
]);

/**
 * Focus an explicit cross-pillar deep link once its real section exists.
 * MutationObserver avoids fragile time-based polling and is disconnected on
 * navigation or after a bounded deadline. Back/Forward changes are handled
 * through React Router's location, without a document reload.
 */
export function PillarDeepLinkFocus() {
  const location = useLocation();

  useEffect(() => {
    const targetId = location.hash.slice(1);
    if (!allowedTargets.has(targetId)) return;

    const focusTarget = (): boolean => {
      const element = document.getElementById(targetId);
      if (!element) return false;

      // Headings are not natively focusable. This temporary programmatic
      // focus target preserves semantic heading roles and keyboard access.
      if (!element.hasAttribute('tabindex')) element.setAttribute('tabindex', '-1');
      element.focus({ preventScroll: true });
      element.scrollIntoView({ behavior: 'auto', block: 'start' });
      return true;
    };

    // Defer until React has committed this route's element tree.
    let observer: MutationObserver | null = null;
    const timer = window.setTimeout(() => {
      if (focusTarget()) return;
      const container = document.getElementById('main-content');
      if (!container) return;
      observer = new MutationObserver(() => {
        if (focusTarget()) observer?.disconnect();
      });
      observer.observe(container, { childList: true, subtree: true });
    }, 0);

    const deadline = window.setTimeout(() => observer?.disconnect(), 10_000);
    return () => {
      window.clearTimeout(timer);
      window.clearTimeout(deadline);
      observer?.disconnect();
    };
  }, [location.pathname, location.hash]);

  return null;
}
