import { useEffect, useMemo, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  getOperatorInteractionHistory,
  type OperatorInteractionHistoryResponse,
  type OperatorInteractionResponse,
} from '../../api/operatorInteractionsApi';
import './RecentOperatorSearches.css';

interface RecentOperatorSearchesProps {
  /** Increments only for confirmed interaction-recorded SignalR messages. */
  readonly refreshToken: number;
}

type SearchEvent = OperatorInteractionResponse & {
  readonly kind: 'SearchSubmitted' | 'FiltersChanged';
};

function isSubmittedSearch(event: OperatorInteractionResponse): event is SearchEvent {
  return event.kind === 'SearchSubmitted' || event.kind === 'FiltersChanged';
}

/**
 * Deterministic, bounded presentation of real server-retained submitted
 * searches. Do not claim that a recent count is a model confidence score or
 * a probability. The history endpoint itself is a read-only GET.
 */
function summariseSearch(event: SearchEvent): string {
  const parts: string[] = [];
  if (event.searchText?.trim()) parts.push(`Text: ${event.searchText.trim()}`);
  if (event.canonicalIdentifier) parts.push(`Identifier: ${event.canonicalIdentifier}`);
  if (event.category) parts.push(`Category: ${event.category}`);
  if (event.status) parts.push(`Status: ${event.status}`);
  if (event.conditionType) parts.push(`Condition: ${event.conditionType}`);
  if (event.hasActiveCondition !== null) parts.push(`Active: ${event.hasActiveCondition}`);
  return parts.length > 0 ? parts.join(' · ') : 'All registered devices';
}

/** Bounded values are sampled from the most recent 50 stored interactions. */
export function RecentOperatorSearches({ refreshToken }: RecentOperatorSearchesProps) {
  const [retry, setRetry] = useState(0);
  const [report, setReport] = useState<OperatorInteractionHistoryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    const timer = window.setTimeout(() => {
      setLoading(true);
      void getOperatorInteractionHistory(50, controller.signal)
        .then((next) => {
          if (!active || controller.signal.aborted) return;
          setReport(next);
          setError(null);
        })
        .catch((reason: unknown) => {
          if (!active || controller.signal.aborted) return;
          setReport(null);
          setError(getApiErrorMessage(reason, 'Unable to read recent operator search evidence.'));
        })
        .finally(() => {
          if (active && !controller.signal.aborted) setLoading(false);
        });
    }, 100);
    return () => {
      active = false;
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [refreshToken, retry]);

  const searches = useMemo(
    () => (report?.events.filter(isSubmittedSearch) ?? []).sort((a, b) => b.sequence - a.sequence),
    [report],
  );

  const trends = useMemo(() => {
    const byCriteria = new Map<string, { label: string; count: number; latest: number }>();
    for (const event of searches) {
      const label = summariseSearch(event);
      const current = byCriteria.get(label);
      if (current) current.count += 1;
      else byCriteria.set(label, { label, count: 1, latest: event.sequence });
    }
    return [...byCriteria.values()]
      .sort((a, b) => b.count - a.count || b.latest - a.latest || a.label.localeCompare(b.label))
      .slice(0, 3);
  }, [searches]);

  return (
    <section
      className="operator-searches"
      aria-labelledby="operator-searches-title"
      data-testid="operator-searches"
    >
      <header className="operator-searches__header">
        <div>
          <p className="realtime-kicker">P3-10 / RECORDED OPERATOR HISTORY</p>
          <h3 id="operator-searches-title">Recent submitted searches</h3>
          <p className="realtime-section-detail">
            Real server-validated submissions only. Counts describe this retained sample, not
            predictive confidence or unsubmitted typing.
          </p>
        </div>
        <button
          className="realtime-action"
          type="button"
          disabled={loading}
          onClick={() => setRetry((value) => value + 1)}
        >
          {loading ? 'Reading…' : 'Refresh search evidence'}
        </button>
      </header>

      {loading && report === null && error === null && (
        <p role="status">Reading bounded operator evidence…</p>
      )}
      {error && (
        <p className="operator-searches__error" role="alert">
          {error}
        </p>
      )}
      {report && (
        <>
          <p className="operator-searches__summary">
            {searches.length} submitted searches or filter changes in the latest{' '}
            {report.events.length} retained events · {report.totalRecorded} events recorded since
            process start.
          </p>
          {searches.length === 0 ? (
            <p className="operator-searches__empty" data-testid="operator-searches-empty">
              No deliberate search submissions in the retained sample. Viewing this panel does not
              record a search or train a learned action.
            </p>
          ) : (
            <>
              <ol className="operator-searches__list" aria-label="Latest submitted search evidence">
                {searches.slice(0, 5).map((event) => (
                  <li key={event.sequence} data-testid="operator-search-event">
                    <span className="operator-searches__sequence">
                      #{event.sequence} / {event.kind}
                    </span>
                    <strong>{summariseSearch(event)}</strong>
                    <span>
                      {event.resultCount ?? 'Unreported'} matching devices ·{' '}
                      {new Date(event.occurredAtUtc).toLocaleString('en-ZA')}
                    </span>
                  </li>
                ))}
              </ol>
              <p className="operator-searches__trends-title">
                Most frequent criteria in retained sample
              </p>
              <ol className="operator-searches__trends">
                {trends.map((trend) => (
                  <li key={trend.label}>
                    <span>{trend.label}</span>
                    <span>{trend.count} submission(s)</span>
                  </li>
                ))}
              </ol>
            </>
          )}
          <p className="operator-searches__note">
            Search history is descriptive operator evidence, not a trained actuator recommendation.
            Learned Suggested Actions require distinct validated alert/search/command episodes.
          </p>
        </>
      )}
    </section>
  );
}
