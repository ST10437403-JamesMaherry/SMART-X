import type { MeshDiagramLayout } from './meshLayout';
import { MESH_DIAGRAM_HEIGHT, MESH_DIAGRAM_WIDTH, sameMeshKey } from './meshLayout';
import type { MeshOverlayDescription } from './meshOverlay';

interface MeshCanvasProps {
  readonly layout: MeshDiagramLayout;
  readonly overlay: MeshOverlayDescription;
  readonly selectedSensorId: string | null;
  readonly onSelectSensor: (sensorId: string) => void;
}

/**
 * Draws the actual graph revision and its requested overlay, without deriving
 * graph routes from the coordinates. Native buttons over each sensor preserve
 * keyboard/Space/Enter support. The text/table alternative is always present.
 */
export function MeshCanvas({ layout, overlay, selectedSensorId, onSelectSensor }: MeshCanvasProps) {
  const points = new Map(layout.nodes.map((point) => [point.node.key.toLowerCase(), point]));
  const currentPath = new Set(overlay.selectedPathKeys.map((key) => key.toLowerCase()));
  const visibleHighlighted = layout.links.filter((link) =>
    overlay.highlightedLinkIds.has(link.id),
  ).length;

  return (
    <section className="mesh-canvas" aria-label="Logical mesh graph">
      <p className="mesh-canvas__instruction" id="mesh-canvas-instruction">
        Logical layout, not physical sensor positions. Select a numbered node to inspect it. On
        narrow screens, scroll inside the diagram; the table view provides a text alternative.
      </p>
      <p className="mesh-canvas__overlay-summary" role="status" aria-live="polite">
        {overlay.explanation}
        {overlay.highlightedLinkIds.size > visibleHighlighted &&
          ` ${overlay.highlightedLinkIds.size - visibleHighlighted} highlighted links are outside the bounded diagram.`}
      </p>
      <div
        className="mesh-canvas__viewport"
        role="region"
        aria-label="Scrollable mesh diagram"
        tabIndex={0}
      >
        <div className="mesh-canvas__board">
          <svg
            className="mesh-canvas__svg"
            viewBox={`0 0 ${MESH_DIAGRAM_WIDTH} ${MESH_DIAGRAM_HEIGHT}`}
            width={MESH_DIAGRAM_WIDTH}
            height={MESH_DIAGRAM_HEIGHT}
            aria-hidden="true"
            focusable="false"
          >
            <line x1="24" y1="508" x2="936" y2="508" className="mesh-canvas__axis" />
            <line x1="90" y1="25" x2="90" y2="495" className="mesh-canvas__axis" />
            {layout.links.map((link) => {
              const first = points.get(link.nodeAKey.toLowerCase());
              const second = points.get(link.nodeBKey.toLowerCase());
              if (!first || !second) {
                return null;
              }

              // Configured links can remain in storage after a sensor goes
              // Offline. A configured line is NOT proof of forwarding ability.
              const ineligible =
                !first.node.isForwardingEligible || !second.node.isForwardingEligible;
              const highlighted = overlay.highlightedLinkIds.has(link.id);
              const edgeState =
                overlay.mode === 'connections'
                  ? ineligible
                    ? 'ineligible'
                    : 'configured'
                  : highlighted
                    ? overlay.mode === 'bfs'
                      ? 'bfs-route'
                      : 'mst-backbone'
                    : ineligible
                      ? 'ineligible'
                      : 'context';
              const centerX = (first.x + second.x) / 2;
              const centerY = (first.y + second.y) / 2;

              return (
                <g key={link.id}>
                  <line
                    x1={first.x}
                    y1={first.y}
                    x2={second.x}
                    y2={second.y}
                    className={`mesh-canvas__edge mesh-canvas__edge--${edgeState}`}
                    data-testid="mesh-diagram-edge"
                    data-link-id={link.id}
                    data-overlay-state={edgeState}
                  />
                  <rect
                    x={centerX - 20}
                    y={centerY - 13}
                    width="40"
                    height="18"
                    rx="1"
                    className={`mesh-canvas__cost-surface${edgeState === 'context' ? ' mesh-canvas__cost-surface--context' : ''}`}
                  />
                  <text
                    x={centerX}
                    y={centerY}
                    className={`mesh-canvas__cost${edgeState === 'context' ? ' mesh-canvas__cost--context' : ''}`}
                  >
                    {link.simulatedEnergyCost.toFixed(2)}
                  </text>
                </g>
              );
            })}
            {layout.nodes.map((point) => {
              const isSelected =
                selectedSensorId !== null &&
                point.node.sensorProfileId !== null &&
                point.node.sensorProfileId.toLowerCase() === selectedSensorId.toLowerCase();
              const isOnBfsPath =
                overlay.mode === 'bfs' && currentPath.has(point.node.key.toLowerCase());
              return (
                <g key={point.node.key}>
                  <circle
                    cx={point.x}
                    cy={point.y}
                    r={isSelected ? 24 : 20}
                    className={`mesh-canvas__vertex mesh-canvas__vertex--${point.connection.toLowerCase()}${isSelected ? ' mesh-canvas__vertex--selected' : ''}${isOnBfsPath ? ' mesh-canvas__vertex--bfs-path' : ''}`}
                  />
                  <text x={point.x} y={point.y + 4} className="mesh-canvas__vertex-label">
                    {point.label}
                  </text>
                  {isSelected && (
                    <circle
                      cx={point.x}
                      cy={point.y}
                      r="30"
                      className="mesh-canvas__selection-ring"
                    />
                  )}
                </g>
              );
            })}
          </svg>

          {layout.nodes
            .filter((point) => point.node.kind === 'Sensor' && point.node.sensorProfileId !== null)
            .map((point) => {
              const sensorId = point.node.sensorProfileId;
              if (!sensorId) {
                return null;
              }
              const isSelected =
                selectedSensorId !== null && sameMeshKey(sensorId, selectedSensorId);
              return (
                <button
                  key={point.node.key}
                  className="mesh-canvas__node-button"
                  type="button"
                  style={{ left: point.x, top: point.y }}
                  aria-label={`Select node ${point.node.name ?? point.node.deviceIdentifier ?? sensorId}, ${point.connection.toLowerCase()}, ${point.node.status ?? 'unknown'} health`}
                  aria-pressed={isSelected}
                  onClick={() => onSelectSensor(sensorId)}
                >
                  <span className="mesh-canvas__visually-hidden">
                    Select sensor {point.node.name ?? sensorId}
                  </span>
                </button>
              );
            })}
        </div>
      </div>
      <div className="mesh-canvas__legend" aria-label="Network node legend">
        <span>
          <i
            className="mesh-canvas__legend-mark mesh-canvas__legend-mark--gateway"
            aria-hidden="true"
          />
          Gateway
        </span>
        <span>
          <i
            className="mesh-canvas__legend-mark mesh-canvas__legend-mark--reachable"
            aria-hidden="true"
          />
          Reachable
        </span>
        <span>
          <i
            className="mesh-canvas__legend-mark mesh-canvas__legend-mark--disconnected"
            aria-hidden="true"
          />
          Disconnected
        </span>
        <span>
          <i
            className="mesh-canvas__legend-mark mesh-canvas__legend-mark--unavailable"
            aria-hidden="true"
          />
          Unavailable
        </span>
      </div>
      <div
        className="mesh-canvas__legend mesh-canvas__legend--routes"
        aria-label="Line style legend"
      >
        <span>
          <i
            className="mesh-canvas__line-mark mesh-canvas__line-mark--selected"
            aria-hidden="true"
          />
          Emphasized API route/tree
        </span>
        <span>
          <i
            className="mesh-canvas__line-mark mesh-canvas__line-mark--context"
            aria-hidden="true"
          />
          Other configured link
        </span>
        <span>
          <i
            className="mesh-canvas__line-mark mesh-canvas__line-mark--inactive"
            aria-hidden="true"
          />
          Unavailable endpoint
        </span>
      </div>
      <p className="mesh-canvas__footnote" aria-live="polite">
        Showing {Math.max(0, layout.nodes.length - 1)} of {layout.matchingSensorCount} matching
        sensors and {layout.links.length} links between the displayed vertices.
        {layout.hiddenSensorCount > 0 &&
          ` ${layout.hiddenSensorCount} sensors are outside this bounded diagram; use search or table view.`}
      </p>
    </section>
  );
}
