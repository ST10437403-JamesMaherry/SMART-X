import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useEffect, useRef, useState } from 'react';

import { buildApiUrl } from '../../api/apiClient';
import type { DashboardChangedResponse } from '../../api/dashboardApi';

/**
 * Represents the current browser connection to the Smart-X dashboard hub.
 */
export type DashboardRealtimeState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/**
 * Callback invoked when the server reports that dashboard state may have
 * changed.
 *
 * Undefined is supplied after a successful SignalR reconnection so callers
 * can reload authoritative state even when notifications were missed while
 * disconnected.
 */
type DashboardChangedHandler = (change?: DashboardChangedResponse) => void;

/**
 * Maintains the SignalR connection used by the Adaptive Anomaly Spotlight.
 *
 * SignalR is only an invalidation channel. The authoritative dashboard state
 * remains the normal HTTP snapshot returned by GET /api/dashboard/issues.
 *
 * Initial connection failures are retried periodically. Once connected,
 * SignalR's automatic reconnect policy handles transient connection loss.
 */
export function useDashboardRealtime(
  onDashboardChanged: DashboardChangedHandler,
): DashboardRealtimeState {
  const [connectionState, setConnectionState] = useState<DashboardRealtimeState>('connecting');

  const callbackRef = useRef<DashboardChangedHandler>(onDashboardChanged);

  /**
   * Keep the current callback available to the long-lived SignalR connection
   * without rebuilding that connection after every component render.
   */
  useEffect(() => {
    callbackRef.current = onDashboardChanged;
  }, [onDashboardChanged]);

  useEffect(() => {
    let disposed = false;
    let retryTimer: number | undefined;

    const connection = new HubConnectionBuilder()
      .withUrl(buildApiUrl('/hubs/dashboard'), {
        /*
         * Smart-X Part 1 has no cookie-based authentication requirement for
         * the dashboard hub. Avoiding credentialed cross-origin requests keeps
         * the existing development CORS policy sufficient.
         */
        withCredentials: false,
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build();

    /**
     * Schedules another initial connection attempt after the hub was
     * unreachable.
     */
    function scheduleRestart(): void {
      if (disposed) {
        return;
      }

      window.clearTimeout(retryTimer);

      retryTimer = window.setTimeout(() => {
        void startConnection();
      }, 5_000);
    }

    /**
     * Starts the hub connection.
     *
     * withAutomaticReconnect handles interruptions after a successful
     * connection. This explicit retry loop handles initial startup failures.
     */
    async function startConnection(): Promise<void> {
      if (disposed) {
        return;
      }

      setConnectionState('connecting');

      try {
        await connection.start();

        if (disposed) {
          return;
        }

        setConnectionState('connected');
      } catch {
        if (disposed) {
          return;
        }

        setConnectionState('disconnected');
        scheduleRestart();
      }
    }

    connection.on('DashboardChanged', (change: DashboardChangedResponse) => {
      if (disposed) {
        return;
      }

      callbackRef.current(change);
    });

    connection.onreconnecting(() => {
      if (!disposed) {
        setConnectionState('reconnecting');
      }
    });

    connection.onreconnected(() => {
      if (disposed) {
        return;
      }

      setConnectionState('connected');

      /*
       * Events may have occurred while the browser was disconnected. Reload
       * the authoritative snapshot immediately after reconnection.
       */
      callbackRef.current(undefined);
    });

    connection.onclose(() => {
      if (disposed) {
        return;
      }

      setConnectionState('disconnected');

      /*
       * Automatic reconnect has been exhausted. Return to the same retry loop
       * used when the initial API connection is unavailable.
       */
      scheduleRestart();
    });

    /*
     * Delay initial startup beyond React's synchronous effect phase, following
     * the same hooks-lint-safe pattern used elsewhere in the Smart-X client.
     */
    const startTimer = window.setTimeout(() => {
      void startConnection();
    }, 0);

    return () => {
      disposed = true;

      window.clearTimeout(startTimer);
      window.clearTimeout(retryTimer);

      connection.off('DashboardChanged');

      void connection.stop();
    };
  }, []);

  return connectionState;
}
