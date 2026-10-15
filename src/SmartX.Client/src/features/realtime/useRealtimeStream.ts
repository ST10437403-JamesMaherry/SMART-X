import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useEffect, useRef, useState } from 'react';

import { buildApiUrl } from '../../api/apiClient';
import type { RealtimeChangedResponse } from '../../api/realtimeApi';

/** The connection status is separate from the last successful HTTP snapshot. */
export type RealtimeConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

type InvalidationHandler = (change?: RealtimeChangedResponse) => void;

/**
 * Listen for lightweight Part 2 invalidations; never accept device state over
 * SignalR. The caller reloads the authoritative HTTP snapshot instead.
 *
 * Automatic reconnect handles interruptions after the first connection.
 * Initial connection failures and exhausted reconnect attempts use a separate
 * bounded-interval retry loop, matching the existing Part 1 hub convention.
 */
export function useRealtimeStream(onInvalidated: InvalidationHandler): RealtimeConnectionState {
  const [state, setState] = useState<RealtimeConnectionState>('connecting');
  const handlerRef = useRef(onInvalidated);

  // Keep the callback current without repeatedly constructing a hub connection.
  useEffect(() => {
    handlerRef.current = onInvalidated;
  }, [onInvalidated]);

  useEffect(() => {
    let disposed = false;
    let starting = false;
    let retryTimer: number | undefined;

    const connection = new HubConnectionBuilder()
      .withUrl(buildApiUrl('/hubs/realtime'), { withCredentials: false })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build();

    function scheduleRetry(): void {
      if (disposed) return;
      window.clearTimeout(retryTimer);
      retryTimer = window.setTimeout(() => void startConnection(), 5_000);
    }

    async function startConnection(): Promise<void> {
      if (disposed || starting || connection.state !== HubConnectionState.Disconnected) return;

      starting = true;
      setState('connecting');

      try {
        await connection.start();
        if (disposed) return;

        setState('connected');
        // Catch changes that happened between the first HTTP GET and hub setup.
        handlerRef.current(undefined);
      } catch {
        if (disposed) return;
        setState('disconnected');
        scheduleRetry();
      } finally {
        starting = false;
      }
    }

    connection.on('RealtimeChanged', (change: RealtimeChangedResponse) => {
      if (!disposed) handlerRef.current(change);
    });

    connection.onreconnecting(() => {
      if (!disposed) setState('reconnecting');
    });

    connection.onreconnected(() => {
      if (disposed) return;
      setState('connected');
      // Events may have been missed while the transport was disconnected.
      handlerRef.current(undefined);
    });

    connection.onclose(() => {
      if (disposed) return;
      setState('disconnected');
      scheduleRetry();
    });

    // Avoid updating React state synchronously inside the effect body.
    const startupTimer = window.setTimeout(() => void startConnection(), 0);

    return () => {
      disposed = true;
      window.clearTimeout(startupTimer);
      window.clearTimeout(retryTimer);
      connection.off('RealtimeChanged');
      void connection.stop();
    };
  }, []);

  return state;
}
