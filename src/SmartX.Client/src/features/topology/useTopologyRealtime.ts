import { useCallback } from 'react';

import type { RealtimeChangedResponse } from '../../api/realtimeApi';
import { useRealtimeStream, type RealtimeConnectionState } from '../realtime/useRealtimeStream';

/**
 * The established RealtimeChanged SignalR event now covers logical mesh
 * writes. Registry and operational-status messages already use sensor-*.
 * Deliberately exclude ordinary telemetry packets: the backend emits a
 * separate sensor-status-changed event when telemetry revives a device.
 */
const TOPOLOGY_CHANGE_REASONS: ReadonlySet<string> = new Set([
  'topology-link-created',
  'topology-link-cost-changed',
  'topology-link-removed',
  'sensor-registered',
  'sensor-updated',
  'sensor-status-changed',
]);

export type TopologyInvalidationHandler = (change?: RealtimeChangedResponse) => void;

/**
 * No payload / undefined means initial hub connection or reconnection.
 * Either case MUST trigger an authoritative GET because messages can have
 * been missed while the connection was not yet established.
 */
export function shouldRefreshTopology(change?: RealtimeChangedResponse): boolean {
  return change === undefined || TOPOLOGY_CHANGE_REASONS.has(change.reason);
}

/**
 * A lightweight, selection-preserving subscription for the P3-08 dashboard.
 * The caller owns its selected device ID; this hook never clears selection,
 * caches graph data, or replaces the four authoritative topology GETs.
 *
 * P3-08 must cancel superseded HTTP requests and reject outdated responses
 * (via request generation plus GraphVersion) to prevent stale route views.
 * Reuses the existing initial-retry and automatic-reconnect implementation.
 */
export function useTopologyRealtime(
  onInvalidated: TopologyInvalidationHandler,
): RealtimeConnectionState {
  const handleRealtimeEvent = useCallback(
    (change?: RealtimeChangedResponse): void => {
      if (shouldRefreshTopology(change)) {
        onInvalidated(change);
      }
    },
    [onInvalidated],
  );

  return useRealtimeStream(handleRealtimeEvent);
}
