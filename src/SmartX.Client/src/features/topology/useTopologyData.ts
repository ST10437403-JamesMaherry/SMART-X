import { useCallback, useEffect, useRef, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import { loadConsistentTopology, type ConsistentTopologyProjection } from '../../api/topologyApi';
import { useWorkspaceState } from '../../state/workspaceState';
import { useTopologyRealtime } from './useTopologyRealtime';

/** A complete projection is published together; partial HTTP responses are not. */
export type TopologyDataPhase = 'loading' | 'ready' | 'error';

interface RequestState {
  readonly requestKey: string;
  readonly phase: TopologyDataPhase;
  readonly projection: ConsistentTopologyProjection | null;
  readonly errorMessage: string | null;
}

/**
 * The existing workspace provider owns selection across /ingestion,
 * /realtime and /topology. The topology view must not create a second sensor
 * selection context or overwrite server identities during refresh.
 */
export function useTopologyData() {
  const { selectedSensorId, setSelectedSensorId } = useWorkspaceState();
  const [refreshSequence, setRefreshSequence] = useState(0);
  const requestGeneration = useRef(0);
  const [requestState, setRequestState] = useState<RequestState>({
    requestKey: '',
    phase: 'loading',
    projection: null,
    errorMessage: null,
  });

  const refreshTopology = useCallback((): void => {
    setRefreshSequence((previous) => previous + 1);
  }, []);

  // Initial connection, selected topology invalidation and hub reconnection
  // all refetch authoritative HTTP projections; SignalR never supplies edges.
  const realtimeState = useTopologyRealtime(refreshTopology);
  const requestKey = `${refreshSequence}:${selectedSensorId ?? ''}`;

  useEffect(() => {
    const controller = new AbortController();
    const generation = ++requestGeneration.current;
    let active = true;

    // Schedule the request after the effect is established. This also avoids
    // a synchronous React state update inside an effect in strict lint mode.
    const startTimer = window.setTimeout(() => {
      void loadConsistentTopology(selectedSensorId, controller.signal)
        .then((projection) => {
          if (!active || controller.signal.aborted || generation !== requestGeneration.current) {
            return;
          }

          setRequestState({
            requestKey,
            phase: 'ready',
            projection,
            errorMessage: null,
          });
        })
        .catch((error: unknown) => {
          if (!active || controller.signal.aborted || generation !== requestGeneration.current) {
            return;
          }

          const fallback = 'Unable to load the current Smart-X mesh topology.';
          const message =
            error instanceof Error && error.name !== 'ApiError'
              ? error.message
              : getApiErrorMessage(error, fallback);

          setRequestState({
            requestKey,
            phase: 'error',
            projection: null,
            errorMessage: message,
          });
        });
    }, 0);

    return () => {
      active = false;
      window.clearTimeout(startTimer);
      controller.abort();
    };
  }, [selectedSensorId, requestKey]);

  // Changing selection or receiving a topology invalidation must immediately
  // hide the old result, even before the effect cancels its superseded fetch.
  const currentState: RequestState =
    requestState.requestKey === requestKey
      ? requestState
      : {
          requestKey,
          phase: 'loading',
          projection: null,
          errorMessage: null,
        };

  return {
    phase: currentState.phase,
    projection: currentState.projection,
    errorMessage: currentState.errorMessage,
    selectedSensorId,
    setSelectedSensorId,
    realtimeState,
    refreshTopology,
  };
}
