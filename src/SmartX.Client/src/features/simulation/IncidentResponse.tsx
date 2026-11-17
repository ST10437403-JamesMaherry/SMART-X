import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  acknowledgeDashboardIssue,
  getDashboardSnapshot,
  type DashboardIssueResponse,
} from '../../api/dashboardApi';
import { routes } from '../../routing/routes';
import type { TelemetryTraceResult } from './telemetryTrace';
import { isSameActiveEpisode, isTraceIssue } from './incidentReview';
import './IncidentResponse.css';

interface IncidentResponseProps {
  readonly trace: TelemetryTraceResult | null;
  readonly enabled: boolean;
  readonly onBusyChange: (busy: boolean) => void;
}

type ReviewState = 'idle' | 'loading' | 'ready' | 'error';

export default function IncidentResponse({ trace, enabled, onBusyChange }: IncidentResponseProps) {
  const [issues, setIssues] = useState<readonly DashboardIssueResponse[]>([]);
  const [status, setStatus] = useState<ReviewState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [reviewed, setReviewed] = useState<DashboardIssueResponse | null>(null);
  const [acknowledging, setAcknowledging] = useState(false);
  const [feedback, setFeedback] = useState<string | null>(null);
  const busyRef = useRef(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const permitted = enabled && trace !== null;

  async function refresh(): Promise<void> {
    if (!trace || !permitted || busyRef.current) return;
    busyRef.current = true;
    onBusyChange(true);
    setStatus('loading');
    setError(null);
    setFeedback(null);
    setReviewed(null);
    try {
      const snapshot = await getDashboardSnapshot(false);
      if (!mounted.current) return;
      setIssues(snapshot.issues.filter((issue) => isTraceIssue(issue, trace)));
      setStatus('ready');
    } catch (reason: unknown) {
      if (!mounted.current) return;
      setStatus('error');
      setError(getApiErrorMessage(reason, 'Unable to refresh live incidents.'));
    } finally {
      busyRef.current = false;
      onBusyChange(false);
    }
  }

  async function confirm(): Promise<void> {
    if (!trace || !permitted || !reviewed || busyRef.current || acknowledging) return;
    busyRef.current = true;
    onBusyChange(true);
    setAcknowledging(true);
    setError(null);
    setFeedback(null);
    let posted = false;

    try {
      const latest = await getDashboardSnapshot(false);
      const current = latest.issues.find((issue) => issue.id === reviewed.id);
      if (!current || !isTraceIssue(current, trace) || !isSameActiveEpisode(reviewed, current)) {
        if (mounted.current) {
          setIssues(latest.issues.filter((issue) => isTraceIssue(issue, trace)));
          setStatus('ready');
          setReviewed(null);
          setFeedback(
            'The incident changed during review. No acknowledgement was sent. Review the refreshed issue list.',
          );
        }
        return;
      }

      posted = true;
      const receipt = await acknowledgeDashboardIssue(current.id);
      const after = await getDashboardSnapshot(false);
      const confirmed = after.issues.find((issue) => issue.id === current.id);
      if (
        receipt.id !== current.id ||
        receipt.state !== 'Acknowledged' ||
        !confirmed ||
        confirmed.state !== 'Acknowledged'
      ) {
        throw new Error('Acknowledgement could not be confirmed from the current server snapshot.');
      }

      if (!mounted.current) return;
      setIssues(after.issues.filter((issue) => isTraceIssue(issue, trace)));
      setStatus('ready');
      setReviewed(null);
      setFeedback(
        'Acknowledged and verified. The condition is still unresolved; acknowledgement does not repair the sensor or execute a command.',
      );
    } catch (reason: unknown) {
      if (!mounted.current) return;
      setReviewed(null);
      setStatus('error');
      setError(
        posted
          ? 'Acknowledgement was attempted but its final server state could not be verified. Refresh incidents before taking any further action. ' +
              getApiErrorMessage(reason, 'Unable to confirm acknowledgement.')
          : getApiErrorMessage(reason, 'Unable to revalidate the incident.'),
      );
    } finally {
      busyRef.current = false;
      if (mounted.current) setAcknowledging(false);
      onBusyChange(false);
    }
  }

  return (
    <section
      className="simulation-section incident-response"
      aria-labelledby="incident-response-title"
    >
      <div className="simulation-section__heading">
        <div>
          <p className="simulation-kicker">STEP 05 / OPERATOR RESPONSE</p>
          <h2 id="incident-response-title">Review an actual incident</h2>
          <p>
            Inspect current conditions for the traced moisture probe. Review a specific server issue
            before explicitly acknowledging it.
          </p>
        </div>
        <span className="simulation-section__tag">MANUAL REVIEW</span>
      </div>

      <p className="simulation-help">
        Acknowledgement records operator awareness. It does not resolve the underlying anomaly,
        change topology, or issue relay commands. If no eligible issue is present, repeat the
        telemetry experiment or examine the detector history in Sensor &amp; telemetry.
      </p>

      <button
        type="button"
        className="simulation-secondary incident-response__refresh"
        disabled={!permitted || status === 'loading' || acknowledging}
        onClick={() => void refresh()}
      >
        {status === 'loading' ? 'Refreshing…' : 'Refresh live incidents'}
      </button>

      {!permitted && (
        <p role="status" className="simulation-help">
          Complete a telemetry trace on the healthy greenhouse fixture before reviewing its
          incidents.
        </p>
      )}

      {status === 'ready' && permitted && (
        <div className="incident-response__list" data-testid="incident-response-list">
          {issues.length === 0 ? (
            <p className="simulation-help">
              No active telemetry-spike or repeated-failure incident is currently reported for this
              probe.
            </p>
          ) : (
            issues.map((issue) => (
              <article className="incident-response__item" key={issue.id}>
                <p className="simulation-kicker">
                  {issue.type} / {issue.severity}
                </p>
                <h3>{issue.title}</h3>
                <p>{issue.detail}</p>
                <dl>
                  <div>
                    <dt>Server state</dt>
                    <dd>{issue.state}</dd>
                  </div>
                  <div>
                    <dt>Occurrences</dt>
                    <dd>{issue.occurrenceCount}</dd>
                  </div>
                  <div>
                    <dt>Issue ID</dt>
                    <dd>{issue.id}</dd>
                  </div>
                </dl>
                {issue.state === 'Active' && (
                  <button
                    type="button"
                    disabled={acknowledging || status === 'loading'}
                    onClick={() => {
                      setReviewed(issue);
                      setFeedback(null);
                      setError(null);
                    }}
                  >
                    Review acknowledgement
                  </button>
                )}
              </article>
            ))
          )}
        </div>
      )}

      {reviewed && permitted && (
        <div className="incident-response__confirm" data-testid="incident-acknowledge-review">
          <h3>Confirm acknowledgement</h3>
          <p>
            Acknowledge the current {reviewed.type} episode for the West-zone moisture probe?
            Smart-X will verify that the exact issue ID is still Active before sending an
            acknowledgement request.
          </p>
          <p>
            This changes the incident to <strong>Acknowledged</strong>, not Resolved. It sends no
            relay command.
          </p>
          <div className="incident-response__actions">
            <button type="button" disabled={acknowledging} onClick={() => void confirm()}>
              {acknowledging ? 'Checking…' : 'Confirm acknowledgement'}
            </button>
            <button type="button" disabled={acknowledging} onClick={() => setReviewed(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {feedback && (
        <p role="status" className="incident-response__feedback">
          {feedback}
        </p>
      )}
      {error && (
        <p role="alert" className="incident-response__error">
          {error}
        </p>
      )}
      <p className="simulation-help">
        To investigate worker history, deliberate actuator commands and the server-maintained Undo
        stack, visit <Link to={routes.realtime}>Real-time Operations</Link>.
      </p>
    </section>
  );
}
