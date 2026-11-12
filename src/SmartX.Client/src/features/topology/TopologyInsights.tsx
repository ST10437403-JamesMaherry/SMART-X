import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  loadVerifiedTopologyInsights,
  type VerifiedTopologyInsights,
} from '../../api/topologyInsightsApi';
import type { MeshVertexResponse } from '../../api/topologyApi';
import { routes } from '../../routing/routes';
import { useWorkspaceState } from '../../state/workspaceState';
import './TopologyInsights.css';

interface TopologyInsightsProps {
  /** The same panel appears in Topology and the Operations console. */
  readonly surface: 'topology' | 'operations';
  /** Change only on graph-related invalidations, not ordinary telemetry. */
  readonly refreshToken: number;
  /** Topology can reuse its own previously verified graph revision. */
  readonly expectedGraphVersion?: number;
  readonly nodes?: readonly MeshVertexResponse[];
}

type RequestState = {
  readonly key: string;
  readonly phase: 'loading' | 'ready' | 'error';
  readonly data: VerifiedTopologyInsights | null;
  readonly error: string | null;
};

const refreshIntervalMs = 30_000;
const visibleAffectedLimit = 5;

function utcTime(value: string): string {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return 'Unavailable';
  return `${new Intl.DateTimeFormat('en-ZA', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  }).format(parsed)} UTC`;
}

/**
 * P3-10 advisory-only interface. A click selects a real, returned UUID and
 * navigates to Topology; it NEVER records an alert view, trains a pattern or
 * invokes the command endpoint. Learned actions remain in SuggestedActions.
 *
 * Each refresh is a cancelable, bounded HTTP read. The panel stays honest on
 * failures instead of presenting old warnings as if they were still current.
 */
export function TopologyInsights({
  surface,
  refreshToken,
  expectedGraphVersion,
  nodes,
}: TopologyInsightsProps) {
  const { setSelectedSensorId } = useWorkspaceState();
  const [retry, setRetry] = useState(0);
  const [periodic, setPeriodic] = useState(0);
  const [state, setState] = useState<RequestState>({
    key: '',
    phase: 'loading',
    data: null,
    error: null,
  });

  // Recovery announcements expire on the server even without a new graph
  // event. Visibility refresh also catches notifications missed during sleep.
  useEffect(() => {
    const interval = window.setInterval(() => {
      if (!document.hidden) setPeriodic((previous) => previous + 1);
    }, refreshIntervalMs);
    const onVisibility = () => {
      if (!document.hidden) setPeriodic((previous) => previous + 1);
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.clearInterval(interval);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  const revision = expectedGraphVersion ?? null;
  const key = `${surface}:${revision ?? 'auto'}:${refreshToken}:${retry}:${periodic}`;

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    // Schedule the state transition outside the synchronous effect body to
    // comply with strict React Hooks lint and avoid transient stale advice.
    const timer = window.setTimeout(() => {
      void loadVerifiedTopologyInsights(revision, nodes ?? null, controller.signal)
        .then((data) => {
          if (!active || controller.signal.aborted) return;
          setState({ key, phase: 'ready', data, error: null });
        })
        .catch((reason: unknown) => {
          if (!active || controller.signal.aborted) return;
          setState({
            key,
            phase: 'error',
            data: null,
            error: getApiErrorMessage(reason, 'Unable to verify live network insights.'),
          });
        });
    }, 100);

    return () => {
      active = false;
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [key, revision, nodes]);

  const current: RequestState =
    state.key === key ? state : { key, phase: 'loading', data: null, error: null };
  const report = current.data?.response;
  const nodeById = new Map(
    current.data?.nodes
      .filter((node) => node.kind === 'Sensor' && node.sensorProfileId !== null)
      .map((node) => [node.sensorProfileId!.toLowerCase(), node] as const) ?? [],
  );

  return (
    <section
      className={`topology-insights topology-insights--${surface}`}
      aria-labelledby={`topology-insights-${surface}-title`}
      data-testid="topology-insights"
    >
      <header className="topology-insights__header">
        <div>
          <p className="topology-insights__eyebrow">P3-10 / CURRENT GRAPH EVIDENCE</p>
          <h3 id={`topology-insights-${surface}-title`}>Live Network Insights</h3>
          <p className="topology-insights__description">
            Rule-derived from current gateway reachability and sensor health. Not learned from
            searches; no command is executed automatically.
          </p>
        </div>
        <button
          className="topology-insights__refresh"
          type="button"
          onClick={() => setRetry((value) => value + 1)}
          disabled={current.phase === 'loading'}
        >
          {current.phase === 'loading' ? 'Checking network…' : 'Refresh network insights'}
        </button>
      </header>

      {current.phase === 'loading' && (
        <p className="topology-insights__message" role="status">
          Checking the authoritative topology revision…
        </p>
      )}
      {current.phase === 'error' && (
        <div className="topology-insights__message" role="alert">
          <p>Current network status could not be verified. No stale alerts are displayed.</p>
          <p>{current.error}</p>
          <button type="button" onClick={() => setRetry((value) => value + 1)}>
            Retry live insight request
          </button>
        </div>
      )}

      {report && (
        <>
          <dl className="topology-insights__meta" aria-label="Current insight evidence">
            <div>
              <dt>Authoritative graph</dt>
              <dd>Revision {report.graphVersion}</dd>
            </div>
            <div>
              <dt>Evaluated at</dt>
              <dd>
                <time dateTime={report.evaluatedAtUtc}>{utcTime(report.evaluatedAtUtc)}</time>
              </dd>
            </div>
            <div>
              <dt>Projection first computed</dt>
              <dd>
                <time dateTime={report.projectionComputedAtUtc}>
                  {utcTime(report.projectionComputedAtUtc)}
                </time>
              </dd>
            </div>
            <div>
              <dt>Gateway disconnected</dt>
              <dd>{report.disconnectedSensorCount} registered sensors</dd>
            </div>
          </dl>

          {report.insights.length === 0 ? (
            <p className="topology-insights__empty" data-testid="topology-insights-empty">
              No active, rule-derived mesh risks for this graph revision. This does not assert
              physical radio health or predict future failures.
            </p>
          ) : (
            <ol
              className="topology-insights__list"
              aria-label="Current topology insights"
              aria-live="polite"
            >
              {report.insights.map((insight) => (
                <li
                  className="topology-insights__item"
                  key={insight.key}
                  data-testid="topology-insight"
                  data-insight-key={insight.key}
                  data-current={String(insight.isCurrent)}
                >
                  <div className="topology-insights__item-heading">
                    <span
                      className={`topology-insights__severity topology-insights__severity--${insight.severity.toLowerCase()}`}
                    >
                      {insight.severity} /{' '}
                      {insight.isCurrent ? 'Current condition' : 'Recovery notice'}
                    </span>
                    <span className="topology-insights__source">Rule-derived · Not learned</span>
                  </div>
                  <h4>{insight.title}</h4>
                  <p className="topology-insights__explanation">{insight.explanation}</p>
                  <p className="topology-insights__action">
                    <strong>Operator guidance:</strong> {insight.recommendedAction}
                  </p>
                  <dl className="topology-insights__facts">
                    <div>
                      <dt>Evidence</dt>
                      <dd>
                        {insight.source} · Graph {insight.evidenceGraphVersion}
                      </dd>
                    </div>
                    <div>
                      <dt>First observed by service</dt>
                      <dd>
                        <time dateTime={insight.firstObservedAtUtc}>
                          {utcTime(insight.firstObservedAtUtc)}
                        </time>
                      </dd>
                    </div>
                    <div>
                      <dt>{insight.expiresAtUtc ? 'Expires' : 'Last confirmed'}</dt>
                      <dd>
                        <time dateTime={insight.expiresAtUtc ?? insight.lastConfirmedAtUtc}>
                          {utcTime(insight.expiresAtUtc ?? insight.lastConfirmedAtUtc)}
                        </time>
                      </dd>
                    </div>
                  </dl>

                  {insight.affectedRegisteredSensorCount > 0 && (
                    <div className="topology-insights__affected">
                      <p>Affected registered devices: {insight.affectedRegisteredSensorCount}</p>
                      <ul>
                        {insight.affectedSensorIds.slice(0, visibleAffectedLimit).map((id) => {
                          const identity = nodeById.get(id.toLowerCase());
                          return (
                            <li key={id} data-testid="insight-affected-sensor" data-sensor-id={id}>
                              <span>{identity?.name ?? id}</span>
                              <Link
                                to={routes.topology}
                                onClick={() => setSelectedSensorId(id)}
                                aria-label={`Inspect registered device ${identity?.name ?? id} in topology`}
                              >
                                Inspect route
                              </Link>
                            </li>
                          );
                        })}
                      </ul>
                      {insight.affectedRegisteredSensorCount > visibleAffectedLimit && (
                        <p className="topology-insights__limited">
                          Showing up to {visibleAffectedLimit} real device identities here. Total
                          counts include the {insight.additionalAffectedSensorCount} IDs omitted by
                          the API's bounded response.
                        </p>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ol>
          )}
          <p className="topology-insights__footer">
            Operational facts and learned Suggested Actions are independent. A ranking or warning is
            not authorization to actuate a device. Values are relative simulation data, not measured
            RF conditions.
          </p>
        </>
      )}
    </section>
  );
}
