import { useCallback, useRef, useState } from 'react';

import { ApiError, getApiErrorMessage } from '../../api/apiTypes';
import { createMeshLink, removeMeshLink, updateMeshLinkCost } from '../../api/topologyApi';
import { changeSensorStatus, type SensorStatus } from '../../api/sensorsApi';

export type TopologySimulationOperation = 'create' | 'cost' | 'remove' | 'health';

export interface TopologySimulationFeedback {
  readonly kind: 'success' | 'error';
  readonly message: string;
}

/**
 * Maintained at TopologyPage level, above the load/ready boundary. An HTTP
 * refresh temporarily unmounts the workspace; the operator's success/error
 * feedback and pending-write guard must survive that refresh.
 */
export function useTopologySimulation(onCommitted: () => void) {
  const [pending, setPending] = useState<TopologySimulationOperation | null>(null);
  const [feedback, setFeedback] = useState<TopologySimulationFeedback | null>(null);
  const busy = useRef(false);

  const reportError = useCallback((message: string) => {
    setFeedback({ kind: 'error', message });
  }, []);

  const run = useCallback(
    async (
      operation: TopologySimulationOperation,
      action: () => Promise<string>,
    ): Promise<void> => {
      // This ref prevents a double-click starting two concurrent mutations,
      // even before React has repainted the pending state.
      if (busy.current) {
        return;
      }

      busy.current = true;
      setPending(operation);
      setFeedback(null);
      try {
        // We only report success once the server acknowledges the write.
        const confirmation = await action();
        setFeedback({ kind: 'success', message: confirmation });
        onCommitted();
      } catch (error: unknown) {
        const message =
          error instanceof ApiError && error.status === 403
            ? `Simulation request forbidden (HTTP 403). ${getApiErrorMessage(error, 'Writes are disabled in this environment.')}`
            : getApiErrorMessage(
                error,
                'The server did not confirm this request. Refresh before retrying to check the latest graph.',
              );
        setFeedback({ kind: 'error', message });
      } finally {
        busy.current = false;
        setPending(null);
      }
    },
    [onCommitted],
  );

  const createLink = useCallback(
    async (first: string, second: string, cost: number): Promise<void> => {
      if (!first || !second || first.toLowerCase() === second.toLowerCase()) {
        reportError('Choose two different registered mesh vertices before creating a link.');
        return;
      }
      if (!Number.isFinite(cost) || cost <= 0) {
        reportError('Simulated link cost must be a finite, positive number.');
        return;
      }
      await run('create', async () => {
        const created = await createMeshLink({
          nodeAKey: first,
          nodeBKey: second,
          simulatedEnergyCost: cost,
        });
        return `Simulated link created (${created.id}). The topology has been refreshed from the API.`;
      });
    },
    [reportError, run],
  );

  const changeCost = useCallback(
    async (id: string, cost: number): Promise<void> => {
      if (!id || !Number.isFinite(cost) || cost <= 0) {
        reportError('Select a configured link and enter a finite, positive cost.');
        return;
      }
      await run('cost', async () => {
        const updated = await updateMeshLinkCost(id, cost);
        return `Simulated link cost confirmed by the API: ${updated.simulatedEnergyCost.toFixed(2)}. The topology has been refreshed.`;
      });
    },
    [reportError, run],
  );

  const deleteLink = useCallback(
    async (id: string): Promise<void> => {
      if (!id) {
        reportError('Select a configured link to remove.');
        return;
      }
      await run('remove', async () => {
        await removeMeshLink(id);
        return 'Simulated link removed by the API. The topology has been refreshed.';
      });
    },
    [reportError, run],
  );

  const changeHealth = useCallback(
    async (sensorId: string, status: SensorStatus): Promise<void> => {
      if (!sensorId) {
        reportError('Select a registered sensor before changing its simulated health.');
        return;
      }
      await run('health', async () => {
        const updated = await changeSensorStatus(sensorId, status);
        return `The API confirmed ${updated.name} is ${updated.status}. Gateway routes have been refreshed.`;
      });
    },
    [reportError, run],
  );

  return { pending, feedback, createLink, changeCost, deleteLink, changeHealth };
}

export type TopologySimulationActions = ReturnType<typeof useTopologySimulation>;
