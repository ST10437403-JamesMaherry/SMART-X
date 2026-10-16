import { useCallback, useEffect, useState } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  getPredictedRecommendations,
  type PredictedRecommendationResponse,
  type PredictedRecommendationsResponse,
} from '../../api/recommendationsApi';
import './SuggestedActions.css';

interface SuggestedActionsProps {
  /**
   * Incremented after the parent has loaded an authoritative HTTP snapshot.
   * Existing SignalR events and reconnects already trigger that snapshot.
   */
  readonly refreshToken: number;
}

/**
 * An empty API result does not distinguish no training history from no
 * currently eligible action. Do not claim either cause as fact in the UI.
 */
const emptyReason =
  'No eligible recommendations right now. Smart-X requires at least three independent ' +
  'alert / search / successful-command episodes within seven days, plus a matching live ' +
  'condition and an Online Boolean actuator that would change state.';

/**
 * A short, bounded fallback covers elapsed-time expiry and missed events.
 * SignalR stays an invalidation channel; every refresh uses authoritative HTTP.
 */
const fallbackRefreshMs = 30_000;
const minimumExpiryDelayMs = 1_000;

function displayUtc(timestamp: string): string {
  const parsed = new Date(timestamp);
  if (Number.isNaN(parsed.getTime())) return 'Unavailable';
  return `${new Intl.DateTimeFormat('en-ZA', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: 'UTC',
    hour12: false,
  }).format(parsed)} UTC`;
}

function actionLabel(command: PredictedRecommendationResponse['commandType']): string {
  return command === 'RelayOn' ? 'Relay On' : 'Relay Off';
}

/**
 * Commit 1 is display-only. The action execution capability is intentionally
 * absent until Commit 2 can reuse the authoritative normal-command API.
 * Showing a recommendation must never count as an operator search or action.
 */
export default function SuggestedActions({ refreshToken }: SuggestedActionsProps) {
  const [snapshot, setSnapshot] = useState<PredictedRecommendationsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [retrySequence, setRetrySequence] = useState(0);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const requestRefresh = useCallback(() => {
    // A hidden tab or failed request must never leave time-expired advice
    // visible merely because no authoritative response was received.
    setNowMs(Date.now());
    setRetrySequence((value) => value + 1);
  }, []);

  // Cancel an obsolete request on new evidence, route unmount or manual retry.
  // A failed refresh retains the last confirmed snapshot, with a visible warning.
  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      async function load(): Promise<void> {
        setLoading(true);
        setError(null);

        try {
          const next = await getPredictedRecommendations(controller.signal);
          if (controller.signal.aborted) return;
          setSnapshot(next);
          setNowMs(Date.now());
        } catch (reason) {
          if (!controller.signal.aborted) {
            setError(getApiErrorMessage(reason, 'Unable to refresh Suggested Actions.'));
          }
        } finally {
          if (!controller.signal.aborted) setLoading(false);
        }
      }
      void load();
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [refreshToken, retrySequence]);

  // Check while the workspace is visible even when there are no recommendations
  // and therefore no nextRefreshAtUtc. Returning to a hidden tab also refreshes.
  useEffect(() => {
    const interval = window.setInterval(() => {
      if (!document.hidden) requestRefresh();
    }, fallbackRefreshMs);
    const onVisibilityChange = () => {
      if (!document.hidden) requestRefresh();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    return () => {
      window.clearInterval(interval);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [requestRefresh]);

  // Expiry is a real eligibility boundary, not just a server-side cache hint.
  // Hide expired rows immediately and request a new authoritative evaluation.
  // A minimum delay prevents repeated requests if clocks disagree slightly.
  useEffect(() => {
    if (!snapshot?.nextRefreshAtUtc) return;

    const expiryMs = Date.parse(snapshot.nextRefreshAtUtc);
    if (!Number.isFinite(expiryMs)) return;

    const remainingMs = expiryMs - Date.now() + 50;
    // A stale date from a clock-skewed server must not cause a 1 Hz loop.
    const delay =
      remainingMs <= 0
        ? fallbackRefreshMs
        : Math.min(Math.max(remainingMs, minimumExpiryDelayMs), fallbackRefreshMs);
    const timer = window.setTimeout(requestRefresh, delay);
    return () => window.clearTimeout(timer);
  }, [snapshot, requestRefresh]);

  // Never present a recommendation whose server-computed eligibility has
  // already expired, including after a failed refresh or missed SignalR event.
  const eligibleActions =
    snapshot?.recommendations.filter((item) => {
      const expiry = Date.parse(item.expiresAtUtc);
      return Number.isFinite(expiry) && expiry > nowMs;
    }) ?? [];

  return (
    <section
      className="realtime-section suggested-actions"
      aria-labelledby="suggested-actions-title"
      data-testid="suggested-actions"
    >
      <header className="realtime-section-header suggested-actions-heading">
        <div>
          <p className="realtime-kicker">08 / EVIDENCE-BASED GUIDANCE</p>
          <h3 id="suggested-actions-title">Suggested Actions</h3>
          <p className="realtime-section-detail">
            Learned from deliberate operator investigation and successful relay commands.
            Suggestions are advisory; nothing runs automatically.
          </p>
        </div>
        <div className="suggested-actions-status">
          <span className="suggested-actions-count" data-testid="suggested-actions-count">
            {eligibleActions.length} eligible
          </span>
          <button
            type="button"
            className="realtime-action"
            disabled={loading}
            onClick={requestRefresh}
          >
            {loading ? 'Checking...' : 'Refresh suggestions'}
          </button>
        </div>
      </header>

      {loading && !snapshot && !error && (
        <p className="realtime-explainer" role="status">
          Evaluating real operator patterns and current actuator eligibility...
        </p>
      )}

      {error && (
        <div className="realtime-inline-error suggested-actions-error" role="alert">
          <p>
            {error}
            {snapshot ? ' Last successful guidance is shown where still unexpired.' : ''}
          </p>
          <button type="button" className="realtime-action" onClick={requestRefresh}>
            Retry suggestions
          </button>
        </div>
      )}

      {snapshot && eligibleActions.length === 0 && (
        <div className="suggested-actions-empty" data-testid="suggested-actions-empty">
          <strong>No eligible actions yet</strong>
          <p>{emptyReason}</p>
        </div>
      )}

      {eligibleActions.length > 0 && (
        <ol className="suggested-actions-list" aria-label="Ranked predictive recommendations">
          {eligibleActions.map((action) => (
            <li
              key={`${action.sourceAlertEpisodeId}:${action.targetActuatorId}:${action.commandType}`}
              className="suggested-actions-item"
              data-testid="suggested-action"
            >
              <div className="suggested-actions-item-top">
                <span className="suggested-actions-rank">RECOMMENDED / {action.commandType}</span>
                <span className="suggested-actions-confidence">
                  {action.confidenceBand} confidence / {action.score} of 100
                </span>
              </div>
              <h4>
                {actionLabel(action.commandType)}: {action.targetActuatorName}
              </h4>
              <p className="suggested-actions-context">
                Trigger: {action.severity} {action.conditionType} on {action.sourceDeviceName}
                {action.movement !== 'NotApplicable' && ` / ${action.movement.toLowerCase()}`}
              </p>
              <p className="suggested-actions-explanation">{action.explanation}</p>
              <dl className="suggested-actions-facts">
                <div>
                  <dt>Verified evidence</dt>
                  <dd>{action.supportingEpisodes} distinct episodes</dd>
                </div>
                <div>
                  <dt>Last learned</dt>
                  <dd>{displayUtc(action.lastLearnedAtUtc)}</dd>
                </div>
                <div>
                  <dt>Re-evaluate by</dt>
                  <dd>{displayUtc(action.expiresAtUtc)}</dd>
                </div>
              </dl>
              <p className="suggested-actions-disclaimer">
                Heuristic support score, not a probability. Operator approval is required before any
                command.
              </p>
            </li>
          ))}
        </ol>
      )}

      {snapshot && (
        <p className="suggested-actions-footer">
          Evaluated by server {displayUtc(snapshot.evaluatedAtUtc)}. Minimum support:{' '}
          {snapshot.minimumSupportingEpisodes} independent episodes within{' '}
          {Math.round(snapshot.evidenceWindowSeconds / 86_400)} days.
          {loading && ' Rechecking eligibility...'}
        </p>
      )}
    </section>
  );
}
