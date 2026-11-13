import { useEffect, useMemo } from 'react';

import type { ConsistentTopologyProjection, MeshVertexResponse } from '../../api/topologyApi';
import { MeshCanvas } from './MeshCanvas';
import { describeMeshOverlay, type MeshOverlayMode } from './meshOverlay';
import { TopologySimulationControls } from './TopologySimulationControls';
import type { TopologySimulationActions } from './useTopologySimulation';
import {
  buildMeshDiagram,
  getMeshConnection,
  matchesMeshSearch,
  sortMeshSensors,
} from './meshLayout';
import { TopologyNodeDetails } from './TopologyNodeDetails';
import './TopologyWorkspace.css';

export type MeshViewMode = 'diagram' | 'table';
const TABLE_PAGE_SIZE = 20;

interface TopologyWorkspaceProps {
  readonly projection: ConsistentTopologyProjection;
  readonly selectedSensorId: string | null;
  readonly onSelectSensor: (sensorId: string | null) => void;
  readonly search: string;
  readonly onSearchChange: (value: string) => void;
  readonly overlayMode: MeshOverlayMode;
  readonly onOverlayModeChange: (value: MeshOverlayMode) => void;
  readonly simulation: TopologySimulationActions;
  readonly view: MeshViewMode;
  readonly onViewChange: (value: MeshViewMode) => void;
  readonly requestedPage: number;
  readonly onPageChange: (page: number) => void;
}

/**
 * One shared authoritative projection powers both the visual and the table.
 * The search and view controls are owned by the persistent TopologyPage so
 * a sensor selection, which refetches its route, cannot reset the operator's
 * filters or the selected view. Rendering is bounded and the table paginated.
 */
export function TopologyWorkspace({
  projection,
  selectedSensorId,
  onSelectSensor,
  search,
  onSearchChange,
  view,
  overlayMode,
  onOverlayModeChange,
  simulation,
  onViewChange,
  requestedPage,
  onPageChange,
}: TopologyWorkspaceProps) {
  const sensors = useMemo(
    () => sortMeshSensors(projection.topology.nodes.filter((node) => node.kind === 'Sensor')),
    [projection.topology.nodes],
  );
  const matches = useMemo(
    () => sensors.filter((node) => matchesMeshSearch(node, search)),
    [sensors, search],
  );
  const layout = useMemo(
    () => buildMeshDiagram(projection, matches, selectedSensorId),
    [projection, matches, selectedSensorId],
  );

  const overlay = useMemo(
    () => describeMeshOverlay(projection, selectedSensorId, overlayMode),
    [projection, selectedSensorId, overlayMode],
  );

  const totalPages = Math.max(1, Math.ceil(matches.length / TABLE_PAGE_SIZE));
  const page = Math.min(requestedPage, totalPages - 1);
  const pageRows = matches.slice(page * TABLE_PAGE_SIZE, (page + 1) * TABLE_PAGE_SIZE);

  const selectedSensor = sensors.find(
    (node) => node.sensorProfileId?.toLowerCase() === selectedSensorId?.toLowerCase(),
  );

  // P3-11: deterministically focus the topology hash target after this
  // asynchronously loaded workspace mounts. Native fragment navigation can
  // scroll to a heading without consistently moving document focus, which is
  // problematic for keyboard and assistive-technology users in an SPA.
  useEffect(() => {
    let focusFrame: number | null = null;

    const focusTopologyHashTarget = () => {
      if (window.location.hash !== '#topology-workspace-title') {
        return;
      }

      if (focusFrame !== null) {
        window.cancelAnimationFrame(focusFrame);
      }

      focusFrame = window.requestAnimationFrame(() => {
        const heading = document.getElementById('topology-workspace-title');

        if (heading instanceof HTMLElement) {
          heading.focus({ preventScroll: true });
        }

        focusFrame = null;
      });
    };

    focusTopologyHashTarget();
    window.addEventListener('hashchange', focusTopologyHashTarget);

    return () => {
      window.removeEventListener('hashchange', focusTopologyHashTarget);

      if (focusFrame !== null) {
        window.cancelAnimationFrame(focusFrame);
      }
    };
  }, []);

  return (
    <section className="topology-workspace" aria-labelledby="topology-workspace-title">
      <div className="topology-workspace__header">
        <div>
          <p className="topology-workspace__eyebrow">NETWORK / EXPLORER</p>
          <h2 id="topology-workspace-title" tabIndex={-1}>
            Explore the mesh
          </h2>
          <p>
            Registered devices, configured connections and routing health from graph revision{' '}
            {projection.graphVersion}.
          </p>
        </div>
        <div className="topology-workspace__revision" aria-label="Current topology revision">
          REV / {String(projection.graphVersion).padStart(4, '0')}
        </div>
      </div>

      <div className="topology-workspace__toolbar">
        <div className="topology-workspace__search">
          <label htmlFor="mesh-search">Search mesh devices</label>
          <input
            id="mesh-search"
            type="search"
            value={search}
            placeholder="Name, MAC, unique ID or sensor UUID"
            onChange={(event) => {
              onSearchChange(event.target.value);
            }}
          />
        </div>
        <div className="topology-workspace__view-group" role="group" aria-label="Topology view">
          <button
            type="button"
            className={
              view === 'diagram' ? 'topology-workspace__view is-active' : 'topology-workspace__view'
            }
            aria-pressed={view === 'diagram'}
            onClick={() => onViewChange('diagram')}
          >
            Graph view
          </button>
          <button
            type="button"
            className={
              view === 'table' ? 'topology-workspace__view is-active' : 'topology-workspace__view'
            }
            aria-pressed={view === 'table'}
            onClick={() => onViewChange('table')}
          >
            Accessible table
          </button>
        </div>
      </div>

      <div
        className="topology-workspace__overlay-modes"
        role="group"
        aria-label="Network connection overlay"
      >
        <button
          type="button"
          aria-pressed={overlayMode === 'connections'}
          className={overlayMode === 'connections' ? 'is-active' : undefined}
          onClick={() => onOverlayModeChange('connections')}
        >
          All configured connections
        </button>
        <button
          type="button"
          aria-pressed={overlayMode === 'bfs'}
          className={overlayMode === 'bfs' ? 'is-active' : undefined}
          onClick={() => onOverlayModeChange('bfs')}
        >
          BFS / Fewest-hop route
        </button>
        <button
          type="button"
          aria-pressed={overlayMode === 'mst'}
          className={overlayMode === 'mst' ? 'is-active' : undefined}
          onClick={() => onOverlayModeChange('mst')}
        >
          Prim / Minimum-cost backbone
        </button>
      </div>

      {view === 'table' && (
        <p className="topology-workspace__table-overlay" role="status">
          {overlay.explanation} Switch to Graph view to inspect highlighted links.
        </p>
      )}

      <p className="topology-workspace__results" role="status">
        {matches.length} of {sensors.length} registered sensors match your search.
        {selectedSensor && ` Selected: ${selectedSensor.name ?? selectedSensor.deviceIdentifier}.`}
      </p>

      <div className="topology-workspace__content">
        <div className="topology-workspace__explorer">
          {view === 'diagram' ? (
            <MeshCanvas
              layout={layout}
              overlay={overlay}
              selectedSensorId={selectedSensorId}
              onSelectSensor={(id) => onSelectSensor(id)}
            />
          ) : (
            <section className="mesh-table" aria-label="Accessible mesh node table">
              <div
                className="mesh-table__scroller"
                tabIndex={0}
                role="region"
                aria-label="Scrollable device table"
              >
                <table>
                  <caption>
                    Registered sensors matching the search. Select a row to inspect its device and
                    routes. Showing {matches.length} matching devices.
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">Sensor</th>
                      <th scope="col">Status</th>
                      <th scope="col">Gateway connection</th>
                      <th scope="col">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pageRows.map((node: MeshVertexResponse) => {
                      const connection = getMeshConnection(node, projection);
                      const isSelected =
                        selectedSensorId !== null &&
                        node.sensorProfileId?.toLowerCase() === selectedSensorId.toLowerCase();
                      return (
                        <tr key={node.key} className={isSelected ? 'is-selected' : undefined}>
                          <th scope="row">
                            <span className="mesh-table__name">{node.name ?? node.key}</span>
                            <span className="mesh-table__identity">
                              {node.deviceIdentifier ?? node.key}
                            </span>
                          </th>
                          <td>{node.status ?? 'Unknown'}</td>
                          <td>
                            <span
                              className={`mesh-status mesh-status--${connection.toLowerCase()}`}
                            >
                              {connection}
                            </span>
                          </td>
                          <td>
                            <button
                              type="button"
                              className="mesh-table__select"
                              aria-pressed={isSelected}
                              onClick={() => onSelectSensor(node.sensorProfileId)}
                            >
                              {isSelected ? 'Selected' : `Select ${node.name ?? node.key}`}
                            </button>
                          </td>
                        </tr>
                      );
                    })}
                    {matches.length === 0 && (
                      <tr>
                        <td colSpan={4}>No sensors match. Try another name or identifier.</td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
              {matches.length > TABLE_PAGE_SIZE && (
                <nav className="mesh-table__pager" aria-label="Device table pagination">
                  <button
                    type="button"
                    disabled={page === 0}
                    onClick={() => onPageChange(Math.max(0, page - 1))}
                  >
                    Previous
                  </button>
                  <span>
                    Page {page + 1} of {totalPages}
                  </span>
                  <button
                    type="button"
                    disabled={page + 1 >= totalPages}
                    onClick={() => onPageChange(Math.min(totalPages - 1, page + 1))}
                  >
                    Next
                  </button>
                </nav>
              )}
            </section>
          )}
        </div>
        <TopologyNodeDetails projection={projection} selectedSensorId={selectedSensorId} />
      </div>

      <p className="topology-workspace__footer">
        This is a deterministic logical layout, never a physical floor plan. The selected overlay
        comes from the API: BFS shows a fewest-hop sensor path; Prim shows the minimum-total-cost
        backbone. Faded lines are configured connections outside that selection, not newly
        discovered radio paths.
      </p>

      <TopologySimulationControls
        projection={projection}
        selectedSensorId={selectedSensorId}
        actions={simulation}
      />
    </section>
  );
}
