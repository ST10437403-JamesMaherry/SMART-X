import { useCallback, useEffect, useRef, useState } from 'react';

import { ApiError, getApiErrorMessage } from '../../api/apiTypes';
import { executeRelayCommand } from '../../api/commandsApi';
import {
  getPredictedRecommendations,
  type PredictedRecommendationResponse,
  type PredictedRecommendationsResponse,
} from '../../api/recommendationsApi';
import './SuggestedActions.css';

interface SuggestedActionsProps {
  /** Changes after a real HTTP snapshot; SignalR transports invalidations only. */
  readonly refreshToken: number;
  /** Select the acted-on target and reload genuine device, command and Undo state. */
  readonly onCommandAccepted: (targetActuatorId: string) => void;
  /** Reload authoritative state following a stale/rejected/unconfirmed operation. */
  readonly onCommandStateInvalidated: () => void;
}

type ActionFeedback = {
  readonly kind: 'success' | 'warning' | 'error';
  readonly message: string;
};

/**
 * Empty results cannot distinguish a new process from insufficient evidence or
 * changed eligibility. The client never manufactures training history.
 */
const emptyReason =
  'No eligible recommendations right now. Smart-X requires at least three independent ' +
  'alert / search / successful-command episodes within seven days, plus a matching live ' +
  'condition and an Online Boolean actuator that would change state.';

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
 * All fields that define recommendation context must still match when the
 * operator confirms. A newer ranking score alone does not change identity.
 */
function recommendationIdentity(action: PredictedRecommendationResponse): string {
  return [
    action.sourceAlertEpisodeId,
    action.sourceDeviceId,
    action.conditionType,
    action.severity,
    action.movement,
    action.targetActuatorId,
    action.commandType,
  ].join(':');
}

function isUnexpired(timestamp: string, nowMs: number): boolean {
  const expiry = Date.parse(timestamp);
  return Number.isFinite(expiry) && expiry > nowMs;
}

/**
 * An operator must deliberately review and confirm a suggestion. Before POST,
 * retrieve a fresh, read-only recommendation snapshot so recovered conditions,
 * changed evidence and expired suggestions cannot be acted on inadvertently.
 *
 * The ONLY mutating path remains executeRelayCommand(), shared with the manual
 * controls. The .NET command service revalidates the target and commanded state
 * at commit time. No browser-created Undo records or optimistic state updates.
 */
export default function SuggestedActions({
  refreshToken,
  onCommandAccepted,
  onCommandStateInvalidated,
}: SuggestedActionsProps) {
  const [snapshot, setSnapshot] = useState<PredictedRecommendationsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [retrySequence, setRetrySequence] = useState(0);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [reviewingIdentity, setReviewingIdentity] = useState<string | null>(null);
  const [submittingIdentity, setSubmittingIdentity] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<ActionFeedback | null>(null);

  // The ref closes the small synchronous interval before React disables buttons.
  // A double click can never submit two relay commands from this component.
  const commandInFlightRef = useRef(false);

  const requestRefresh = useCallback(() => {
    setNowMs(Date.now());
    setRetrySequence((value) => value + 1);
  }, []);

  // Cancel obsolete passive GETs; retained guidance is filtered by its expiry.
  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      async function load(): Promise<void> {
        setLoading(true);
        // Do not remove an existing retry control merely because a passive
        // refresh has started. Keep the error until a real GET succeeds.

        try {
          const next = await getPredictedRecommendations(controller.signal);
          if (controller.signal.aborted) return;

          const now = Date.now();
          setSnapshot(next);
          setError(null);
          setNowMs(now);
          // A new server snapshot cannot leave an obsolete approval prompt open.
          setReviewingIdentity((previous) =>
            previous &&
            !next.recommendations.some(
              (item) =>
                recommendationIdentity(item) === previous && isUnexpired(item.expiresAtUtc, now),
            )
              ? null
              : previous,
          );
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

  // SignalR/reconnect refreshes are complemented by visibility and time-based
  // refreshes, including when there are no recommendations to expire.
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

  useEffect(() => {
    if (!snapshot?.nextRefreshAtUtc) return;

    const expiryMs = Date.parse(snapshot.nextRefreshAtUtc);
    if (!Number.isFinite(expiryMs)) return;

    const remainingMs = expiryMs - Date.now() + 50;
    const delay =
      remainingMs <= 0
        ? fallbackRefreshMs
        : Math.min(Math.max(remainingMs, minimumExpiryDelayMs), fallbackRefreshMs);
    const timer = window.setTimeout(requestRefresh, delay);
    return () => window.clearTimeout(timer);
  }, [snapshot, requestRefresh]);

  /**
   * Review is not execution. Confirm rechecks recommendation eligibility via
   * GET, then uses the SAME command endpoint as the manual Relay On/Off buttons.
   * Failed HTTP delivery is intentionally described as unconfirmed: the server
   * might have committed even if its response could not reach the browser.
   */
  // This is invoked by explicit operator confirmation, never during render.
  // useCallback makes the event boundary explicit to React purity analysis.
  const confirmRecommendation = useCallback(
    async (action: PredictedRecommendationResponse): Promise<void> => {
      if (commandInFlightRef.current) return;

      const identity = recommendationIdentity(action);
      commandInFlightRef.current = true;
      setSubmittingIdentity(identity);
      setFeedback(null);
      let commandWasSent = false;

      try {
        // Never POST on the basis of a possibly expired displayed recommendation.
        if (!isUnexpired(action.expiresAtUtc, Date.now())) {
          setFeedback({
            kind: 'error',
            message:
              'This recommendation has expired. No command was sent. Refresh the suggestions.',
          });
          setReviewingIdentity(null);
          requestRefresh();
          return;
        }

        // This GET also detects source recovery or a different current episode.
        const current = await getPredictedRecommendations();
        const now = Date.now();
        const eligible = current.recommendations.some(
          (item) =>
            recommendationIdentity(item) === identity &&
            isUnexpired(item.expiresAtUtc, now) &&
            item.supportingEpisodes >= current.minimumSupportingEpisodes,
        );

        setSnapshot(current);
        setNowMs(now);
        if (!eligible) {
          setReviewingIdentity(null);
          setFeedback({
            kind: 'error',
            message:
              'This recommendation is no longer eligible in the latest server evaluation. ' +
              'No command was sent.',
          });
          onCommandStateInvalidated();
          return;
        }

        // No suggestion-specific endpoint, fake state or client-generated audit.
        commandWasSent = true;
        const result = await executeRelayCommand(action.targetActuatorId, action.commandType);

        if (result.command.stateChanged) {
          setFeedback({
            kind: 'success',
            message:
              `${action.commandType} accepted by server for ${action.targetActuatorName}: ` +
              `${result.command.previousState} to ${result.command.resultingState}. ` +
              'The target is now selected. Undo is available through its normal actuator controls.',
          });
        } else {
          // A competing operator may have changed state between GET and POST.
          // The backend audits an accepted no-op but does NOT push onto the Stack.
          setFeedback({
            kind: 'warning',
            message:
              `${action.commandType} was accepted as a no-op for ${action.targetActuatorName}. ` +
              'No new Undo entry was created. Inspect the server command audit.',
          });
        }

        setReviewingIdentity(null);
        onCommandAccepted(action.targetActuatorId);
        requestRefresh();
      } catch (reason) {
        const detail = getApiErrorMessage(reason, 'The server could not confirm this command.');
        const message = !commandWasSent
          ? `Unable to revalidate this recommendation: ${detail} No command was sent.`
          : reason instanceof ApiError
            ? `The command was rejected by the server: ${detail} Refresh the target state before retrying.`
            : `The command outcome could not be confirmed: ${detail} Inspect the authoritative command audit before retrying.`;

        setFeedback({ kind: 'error', message });
        setReviewingIdentity(null);
        // An unsuccessful or unknown outcome still requires a real state refresh.
        onCommandStateInvalidated();
        requestRefresh();
      } finally {
        commandInFlightRef.current = false;
        setSubmittingIdentity(null);
      }
    },
    [onCommandAccepted, onCommandStateInvalidated, requestRefresh],
  );

  const eligibleActions =
    snapshot?.recommendations.filter((item) => isUnexpired(item.expiresAtUtc, nowMs)) ?? [];

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
            disabled={loading || submittingIdentity !== null}
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

      {feedback && (
        <div
          className={`suggested-actions-result suggested-actions-result--${feedback.kind}`}
          data-testid="suggested-action-result"
          role={feedback.kind === 'error' ? 'alert' : 'status'}
        >
          <p>{feedback.message}</p>
          {feedback.kind === 'success' && (
            <a href="#realtime-command-title">Go to actuator controls and Undo</a>
          )}
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
          {eligibleActions.map((action) => {
            const identity = recommendationIdentity(action);
            const reviewing = reviewingIdentity === identity;
            const submitting = submittingIdentity === identity;

            return (
              <li key={identity} className="suggested-actions-item" data-testid="suggested-action">
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
                  Heuristic support score, not a probability. Operator approval is required before
                  any command.
                </p>

                {!reviewing && (
                  <button
                    type="button"
                    className="realtime-action suggested-actions-review-button"
                    disabled={loading || submittingIdentity !== null}
                    aria-expanded={false}
                    onClick={() => {
                      setReviewingIdentity(identity);
                      setFeedback(null);
                    }}
                  >
                    Review {actionLabel(action.commandType)}
                  </button>
                )}

                {reviewing && (
                  <div
                    className="suggested-actions-approval"
                    role="group"
                    aria-label="Confirm recommended relay command"
                  >
                    <p className="suggested-actions-approval-label">OPERATOR CONFIRMATION</p>
                    <p>
                      Send <strong>{action.commandType}</strong> to{' '}
                      <strong>{action.targetActuatorName}</strong>? Smart-X will check the
                      recommendation again and execute through the standard command API. The server
                      makes the final eligibility and state decision.
                    </p>
                    <div className="suggested-actions-approval-controls">
                      <button
                        type="button"
                        className="realtime-action realtime-action--primary"
                        disabled={loading || submittingIdentity !== null}
                        onClick={() => void confirmRecommendation(action)}
                      >
                        {submitting
                          ? 'Verifying and sending...'
                          : `Confirm ${actionLabel(action.commandType)}`}
                      </button>
                      <button
                        type="button"
                        className="realtime-action"
                        disabled={submittingIdentity !== null}
                        onClick={() => setReviewingIdentity(null)}
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                )}
              </li>
            );
          })}
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
