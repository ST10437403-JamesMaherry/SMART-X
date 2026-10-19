import { useEffect, useRef, useState, type FormEvent } from 'react';

import { getApiErrorMessage } from '../../api/apiTypes';
import {
  deviceSearchPageSize,
  filtersFromSearchResponse,
  getDeviceSearch,
  initialDeviceSearchFilters,
  sameDeviceSearchFilters,
  type DeviceSearchFilters,
  type DeviceSearchItemResponse,
  type DeviceSearchResponse,
} from '../../api/deviceSearchApi';
import {
  recordSearchInteraction,
  type OperatorInteractionResponse,
  type SubmittedSearchInteractionKind,
} from '../../api/operatorInteractionsApi';
import type { RealtimeActiveConditionResponse } from '../../api/realtimeApi';
import { identifierKinds, sensorCategories, sensorStatuses } from '../../api/sensorsApi';
import { useWorkspaceState } from '../../state/workspaceState';
import './DeviceSearch.css';

interface DeviceSearchProps {
  /** Incremented after successful server snapshots / SignalR invalidations. */
  readonly refreshToken: number;
  readonly activeConditions: readonly RealtimeActiveConditionResponse[];
  readonly onInspectDevice: (deviceId: string) => Promise<void>;
  readonly onInspectAlert: (deviceId: string, alertEpisodeId: string) => Promise<void>;
  /** Selection after the server confirms an explicit source-alert association. */
  readonly onSelectAssociatedDevice: (deviceId: string) => void;
}

const conditionTypes = [
  'TelemetrySpike',
  'SensorDisconnected',
  'RepeatedFailure',
  'ConfigurationIssue',
  'SensorStateChange',
] as const;

/**
 * Search/filter drafts, submitted criteria, page offset and alert context
 * survive route navigation in the existing provider. Only results are local:
 * every result is retrieved again from the actual search API.
 *
 * Passive GETs on mount, SignalR refresh and pagination never POST evidence.
 * Submitting a search, applying changed filters, inspecting a device/alert,
 * and linking a searched actuator to an already viewed alert are deliberate.
 */
export default function DeviceSearch({
  refreshToken,
  activeConditions,
  onInspectDevice,
  onInspectAlert,
  onSelectAssociatedDevice,
}: DeviceSearchProps) {
  const {
    selectedSensorId,
    deviceSearchDraft,
    setDeviceSearchDraft,
    deviceSearchApplied,
    setDeviceSearchApplied,
    deviceSearchOffset,
    setDeviceSearchOffset,
    sourceAlertEpisodeId,
    setSourceAlertEpisodeId,
  } = useWorkspaceState();

  const [result, setResult] = useState<DeviceSearchResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [readError, setReadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [recorded, setRecorded] = useState<OperatorInteractionResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [paging, setPaging] = useState(false);
  const [busyResultId, setBusyResultId] = useState<string | null>(null);
  const [retrySequence, setRetrySequence] = useState(0);
  const submitInFlight = useRef(false);
  const pagingRequestRef = useRef<AbortController | null>(null);
  const backgroundRequestRef = useRef<AbortController | null>(null);
  const confirmedPageOffsetRef = useRef<number | null>(null);

  // A route change cancels any operator-requested page load; it must never
  // overwrite the result of a subsequent mount or a new submitted search.
  useEffect(() => {
    return () => {
      pagingRequestRef.current?.abort();
    };
  }, []);

  // An alert episode ID denotes one genuine, currently unresolved incident.
  // A recovered episode stays in the provider for context but cannot be
  // associated with new searches until an operator chooses an active one.
  const sourceAlert = activeConditions.find((item) => item.issueId === sourceAlertEpisodeId);
  const filtersChanged = !sameDeviceSearchFilters(deviceSearchDraft, deviceSearchApplied);

  useEffect(() => {
    // An explicit Next/Previous action already fetched and validated this
    // offset before committing it to the route-retained workspace provider.
    // Do not launch a duplicate GET merely because that offset was persisted.
    if (confirmedPageOffsetRef.current === deviceSearchOffset) {
      confirmedPageOffsetRef.current = null;
      return;
    }

    // Explicit pagination has already made an authoritative read for its
    // destination. A concurrent SignalR refresh must not interrupt it.
    if (pagingRequestRef.current !== null) return;

    const controller = new AbortController();
    backgroundRequestRef.current = controller;
    const timer = window.setTimeout(() => {
      async function read(): Promise<void> {
        setLoading(true);
        setReadError(null);
        try {
          const next = await getDeviceSearch(
            deviceSearchApplied,
            deviceSearchPageSize,
            deviceSearchOffset,
            controller.signal,
          );
          // A deliberately requested page takes priority over a background
          // SignalR/HTTP refresh of the previous offset.
          if (!controller.signal.aborted && pagingRequestRef.current === null) {
            setResult(next);
          }
        } catch (reason) {
          if (!controller.signal.aborted && pagingRequestRef.current === null) {
            setReadError(getApiErrorMessage(reason, 'Unable to load device search results.'));
          }
        } finally {
          if (backgroundRequestRef.current === controller) {
            backgroundRequestRef.current = null;
          }
          if (!controller.signal.aborted) setLoading(false);
        }
      }
      void read();
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
      if (backgroundRequestRef.current === controller) {
        backgroundRequestRef.current = null;
      }
    };
  }, [deviceSearchApplied, deviceSearchOffset, refreshToken, retrySequence]);

  function updateDraft(patch: Partial<DeviceSearchFilters>): void {
    setDeviceSearchDraft((current) => ({ ...current, ...patch }));
  }

  /**
   * Page changes are read-only operations. Fetch the requested page before
   * changing the persisted offset, so a failed/aborted GET cannot leave the
   * operator looking at page 1 while the provider claims page 2 is selected.
   *
   * The server determines the actual page membership. Never sort or slice
   * the current 25 browser rows to fabricate a later page, and never POST an
   * interaction merely because the operator paged through results.
   */
  async function navigatePage(offset: number): Promise<void> {
    // A passive refresh may start after the browser enables the page button
    // but before the click handler runs. It must never swallow that click.
    if (submitting || pagingRequestRef.current !== null || !result) return;
    if (offset < 0 || offset >= result.totalMatches || offset === result.offset) return;

    // The explicit page request wins over any in-flight read of the old page.
    // Abort also prevents a stale refresh from hiding the new page behind a
    // persistent "Refreshing" status when SignalR bursts are in progress.
    backgroundRequestRef.current?.abort();
    backgroundRequestRef.current = null;
    setLoading(false);

    const controller = new AbortController();
    pagingRequestRef.current = controller;
    setPaging(true);
    setReadError(null);

    try {
      const next = await getDeviceSearch(
        deviceSearchApplied,
        deviceSearchPageSize,
        offset,
        controller.signal,
      );

      if (controller.signal.aborted) return;
      if (next.offset !== offset || next.limit !== deviceSearchPageSize) {
        throw new Error('The server returned an unexpected device search page.');
      }

      // The display and saved navigation state are updated as one confirmed
      // React transaction. The following effect skips only this redundant GET.
      confirmedPageOffsetRef.current = next.offset;
      setResult(next);
      setDeviceSearchOffset(next.offset);
    } catch (reason) {
      if (!controller.signal.aborted) {
        setReadError(getApiErrorMessage(reason, 'Unable to load the requested result page.'));
      }
    } finally {
      if (pagingRequestRef.current === controller) {
        pagingRequestRef.current = null;
        if (!controller.signal.aborted) setPaging(false);
      }
    }
  }

  /**
   * Perform a validated GET first; only a successful deliberate submission
   * is sent to the interaction POST endpoint. Never retry a POST silently:
   * after an uncertain network error the server may already have recorded it.
   */
  async function submitSearch(
    kind: SubmittedSearchInteractionKind,
    filters: DeviceSearchFilters = deviceSearchDraft,
  ): Promise<void> {
    if (submitInFlight.current || pagingRequestRef.current !== null) return;
    if (kind === 'FiltersChanged' && sameDeviceSearchFilters(filters, deviceSearchApplied)) return;

    submitInFlight.current = true;
    setSubmitting(true);
    setActionError(null);
    setRecorded(null);

    let searchSucceeded = false;
    try {
      const found = await getDeviceSearch(filters, deviceSearchPageSize, 0);
      searchSucceeded = true;

      // Store only normalised criteria in app-lifetime state. This also
      // updates the results through the ordinary read-only effect.
      const normalized = filtersFromSearchResponse(found);
      setResult(found);
      setDeviceSearchApplied(normalized);
      setDeviceSearchDraft(normalized);
      setDeviceSearchOffset(0);

      // Include existing selected-device context only when that real device
      // belongs to the submitted result page. A source alert is never
      // associated implicitly; it requires its own explicit user action.
      const selectedInPage = found.devices.some((item) => item.device.id === selectedSensorId);
      const event = await recordSearchInteraction(
        kind,
        found,
        selectedInPage ? selectedSensorId : null,
      );
      setRecorded(event); // Only an actual 201 response is displayed as recorded.
    } catch (reason) {
      const detail = getApiErrorMessage(reason, 'The device search could not complete.');
      setActionError(
        searchSucceeded
          ? `Results loaded, but the interaction could not be confirmed. ${detail}`
          : `Search rejected or unavailable. Previous results retained. ${detail}`,
      );
    } finally {
      submitInFlight.current = false;
      setSubmitting(false);
    }
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault();
    void submitSearch('SearchSubmitted');
  }

  /** Explicit inspection: the parent emits a genuine server-validated view. */
  async function inspect(item: DeviceSearchItemResponse, alert: boolean): Promise<void> {
    if (busyResultId !== null) return;
    setBusyResultId(item.device.id);
    try {
      if (alert && item.highestPriorityIssueId) {
        await onInspectAlert(item.device.id, item.highestPriorityIssueId);
      } else {
        await onInspectDevice(item.device.id);
      }
    } finally {
      setBusyResultId(null);
    }
  }

  /**
   * Associate a searched actuator with the alert *previously* viewed by the
   * operator. The P2-08 backend validates result-page membership and that the
   * source episode is still active. This does not claim a command executed.
   */
  async function associate(item: DeviceSearchItemResponse): Promise<void> {
    if (!result || !sourceAlert || busyResultId !== null) return;
    setBusyResultId(item.device.id);
    setActionError(null);
    try {
      const event = await recordSearchInteraction(
        'SearchSubmitted',
        result,
        item.device.id,
        sourceAlert.issueId,
      );
      setRecorded(event);
      onSelectAssociatedDevice(item.device.id);
    } catch (reason) {
      setActionError(
        getApiErrorMessage(reason, 'Could not associate this actuator with the active alert.'),
      );
    } finally {
      setBusyResultId(null);
    }
  }

  const first = result && result.totalMatches > 0 ? result.offset + 1 : 0;
  const last = result ? result.offset + result.devices.length : 0;

  return (
    <section className="realtime-section realtime-search" aria-labelledby="realtime-search-title">
      <header className="realtime-section-header realtime-search-heading">
        <div>
          <p className="realtime-kicker">DISCOVERY / CANONICAL DEVICE SEARCH</p>
          <h3 id="realtime-search-title">Find devices</h3>
          <p className="realtime-section-detail">
            Exact identifiers use the canonical Dictionary. Combined filters query live devices and
            unresolved conditions, without another device index.
          </p>
        </div>
        {sourceAlertEpisodeId && (
          <div className="realtime-source-alert" role="status">
            <strong>
              {sourceAlert ? 'Source alert selected' : 'Source alert no longer active'}
            </strong>
            <span>{sourceAlert?.conditionType ?? sourceAlertEpisodeId.slice(0, 8)}</span>
            <button type="button" onClick={() => setSourceAlertEpisodeId(null)}>
              Clear alert context
            </button>
          </div>
        )}
      </header>

      <form
        className="realtime-search-form"
        onSubmit={handleSubmit}
        aria-label="Device discovery filters"
      >
        <div className="realtime-search-fields">
          <label className="realtime-search-field realtime-search-field--wide">
            <span>Name / deployment text</span>
            <input
              type="search"
              name="device-search-text"
              value={deviceSearchDraft.text}
              maxLength={120}
              disabled={submitting}
              placeholder="Search name, zone or node"
              onChange={(event) => updateDraft({ text: event.target.value })}
            />
          </label>
          <label className="realtime-search-field realtime-search-field--wide">
            <span>Exact physical identifier</span>
            <input
              type="text"
              name="device-search-identifier"
              value={deviceSearchDraft.deviceIdentifier}
              disabled={submitting}
              placeholder="MAC address or unique ID"
              onChange={(event) => updateDraft({ deviceIdentifier: event.target.value })}
            />
          </label>
          <label className="realtime-search-field">
            <span>Identifier kind</span>
            <select
              name="device-search-identifier-kind"
              value={deviceSearchDraft.identifierKind}
              disabled={submitting}
              onChange={(event) =>
                updateDraft({
                  identifierKind: event.target.value as DeviceSearchFilters['identifierKind'],
                })
              }
            >
              {identifierKinds.map((kind) => (
                <option key={kind} value={kind}>
                  {kind}
                </option>
              ))}
            </select>
          </label>
          <label className="realtime-search-field">
            <span>Category</span>
            <select
              name="device-search-category"
              value={deviceSearchDraft.category}
              disabled={submitting}
              onChange={(event) =>
                updateDraft({ category: event.target.value as DeviceSearchFilters['category'] })
              }
            >
              <option value="">Any category</option>
              {sensorCategories.map((category) => (
                <option key={category} value={category}>
                  {category}
                </option>
              ))}
            </select>
          </label>
          <label className="realtime-search-field">
            <span>Operational status</span>
            <select
              name="device-search-status"
              value={deviceSearchDraft.status}
              disabled={submitting}
              onChange={(event) =>
                updateDraft({ status: event.target.value as DeviceSearchFilters['status'] })
              }
            >
              <option value="">Any status</option>
              {sensorStatuses.map((status) => (
                <option key={status} value={status}>
                  {status}
                </option>
              ))}
            </select>
          </label>
          <label className="realtime-search-field">
            <span>Active conditions</span>
            <select
              name="device-search-active"
              value={deviceSearchDraft.hasActiveCondition}
              disabled={submitting}
              onChange={(event) => {
                const value = event.target.value as DeviceSearchFilters['hasActiveCondition'];
                updateDraft({
                  hasActiveCondition: value,
                  // Incompatible combinations are prevented in the UI and
                  // remain independently validated by the backend.
                  conditionType: value === 'false' ? '' : deviceSearchDraft.conditionType,
                });
              }}
            >
              <option value="">Any condition state</option>
              <option value="true">Has active condition</option>
              <option value="false">No active conditions</option>
            </select>
          </label>
          <label className="realtime-search-field">
            <span>Condition type</span>
            <select
              name="device-search-condition-type"
              value={deviceSearchDraft.conditionType}
              disabled={submitting || deviceSearchDraft.hasActiveCondition === 'false'}
              onChange={(event) =>
                updateDraft({
                  conditionType: event.target.value as DeviceSearchFilters['conditionType'],
                })
              }
            >
              <option value="">Any type</option>
              {conditionTypes.map((kind) => (
                <option key={kind} value={kind}>
                  {kind}
                </option>
              ))}
            </select>
          </label>
        </div>

        <div className="realtime-search-actions">
          <button
            className="realtime-action realtime-action--primary"
            type="submit"
            disabled={submitting || paging}
          >
            {submitting ? 'Submitting...' : 'Search devices'}
          </button>
          <button
            className="realtime-action"
            type="button"
            disabled={submitting || paging || !filtersChanged}
            onClick={() => void submitSearch('FiltersChanged')}
          >
            Apply filters
          </button>
          <button
            className="realtime-action"
            type="button"
            disabled={
              submitting ||
              paging ||
              sameDeviceSearchFilters(deviceSearchApplied, initialDeviceSearchFilters)
            }
            onClick={() => void submitSearch('FiltersChanged', initialDeviceSearchFilters)}
          >
            Clear filters
          </button>
          <p>
            Only explicit submissions and views record evidence. Typing, paging, and live refreshes
            do not.
          </p>
        </div>
      </form>

      {recorded && (
        <p className="realtime-search-receipt" role="status" data-testid="search-recorded-receipt">
          Server recorded {recorded.kind} #{recorded.sequence}
          {recorded.deviceId ? ' with selected-device context' : ''}
          {recorded.alertEpisodeId ? ' and a source alert episode' : ''}.
        </p>
      )}
      {actionError && (
        <p className="realtime-inline-error" role="alert">
          {actionError}
        </p>
      )}
      {readError && (
        <div className="realtime-inline-error" role="alert">
          <p>Results refresh failed. Last successful page retained. {readError}</p>
          <button
            type="button"
            className="realtime-action"
            onClick={() => setRetrySequence((n) => n + 1)}
          >
            Retry results
          </button>
        </div>
      )}

      <div className="realtime-search-summary" aria-live="polite">
        <span data-testid="device-search-count">
          {result ? `${result.totalMatches} matching devices` : 'Awaiting device results'}
        </span>
        <span>
          {paging
            ? 'Loading requested page...'
            : loading
              ? 'Refreshing read-only results...'
              : result
                ? `Showing ${first}-${last}`
                : 'No results loaded'}
        </span>
      </div>

      {result && result.devices.length === 0 && (
        <p className="realtime-empty" data-testid="device-search-empty">
          No devices match the applied query. Adjust a filter and submit again.
        </p>
      )}

      {result && result.devices.length > 0 && (
        <ol className="realtime-search-results" aria-label="Device search results">
          {result.devices.map((item) => (
            <li
              className="realtime-search-result"
              key={item.device.id}
              data-testid="device-search-result"
            >
              <div className="realtime-search-device-info">
                <strong>{item.device.name}</strong>
                <span>{item.device.deviceIdentifier}</span>
                <span>
                  {item.device.category} / {item.device.status}
                </span>
                <span>
                  {item.activeConditionCount} unresolved condition(s)
                  {item.highestPriorityConditionType
                    ? ` / ${item.highestPriorityConditionType}`
                    : ''}
                </span>
                {item.device.id === selectedSensorId && (
                  <span className="realtime-search-selected">Selected</span>
                )}
              </div>
              <div className="realtime-search-result-actions">
                <button
                  className="realtime-action"
                  type="button"
                  disabled={busyResultId !== null || submitting || paging}
                  onClick={() => void inspect(item, false)}
                >
                  Inspect device
                </button>
                {item.highestPriorityIssueId && (
                  <button
                    className="realtime-action"
                    type="button"
                    disabled={busyResultId !== null || submitting || paging}
                    onClick={() => void inspect(item, true)}
                  >
                    View active alert
                  </button>
                )}
                {sourceAlert && item.device.category === 'Actuator' && (
                  <button
                    className="realtime-action"
                    type="button"
                    disabled={busyResultId !== null || submitting || paging}
                    onClick={() => void associate(item)}
                  >
                    Associate with source alert
                  </button>
                )}
              </div>
            </li>
          ))}
        </ol>
      )}

      {result && result.totalMatches > deviceSearchPageSize && (
        <nav className="realtime-search-pagination" aria-label="Device search pagination">
          <button
            className="realtime-action"
            type="button"
            disabled={submitting || paging || result.offset === 0}
            onClick={() => void navigatePage(Math.max(0, result.offset - deviceSearchPageSize))}
          >
            Previous page
          </button>
          <span>Page {Math.floor(result.offset / deviceSearchPageSize) + 1}</span>
          <button
            className="realtime-action"
            type="button"
            disabled={submitting || paging || !result.hasMore}
            onClick={() => void navigatePage(result.offset + deviceSearchPageSize)}
          >
            Next page
          </button>
        </nav>
      )}
    </section>
  );
}
