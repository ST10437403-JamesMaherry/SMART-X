using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
using SmartX.Application.Dashboard;
using SmartX.Application.Sensors;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes the authoritative Adaptive Anomaly Spotlight issue lifecycle.
///
/// Part 1 retains its original issue snapshot and acknowledgement routes.
/// P2-06 adds a compact read-only real-time projection of the same active
/// conditions for device-facing operators and the future P2-07 dashboard.
///
/// Both snapshot routes run the same existing configuration reconciliation
/// and use the same DashboardIssueService / HashSet-backed collection.
///
/// Neither route creates another alert store or reprocesses telemetry.
/// </summary>
[ApiController]
[Route("api/dashboard/issues")]
public sealed class DashboardIssuesController : ControllerBase
{
    private readonly SensorManagementService _sensorManagementService;
    private readonly AdaptiveAnomalyAnalysisService _analysisService;
    private readonly DashboardIssueService _dashboardIssueService;
    private readonly DashboardEventCoordinator _dashboardEvents;

    public DashboardIssuesController(
        SensorManagementService sensorManagementService,
        AdaptiveAnomalyAnalysisService analysisService,
        DashboardIssueService dashboardIssueService,
        DashboardEventCoordinator dashboardEvents)
    {
        _sensorManagementService =
            sensorManagementService ??
            throw new ArgumentNullException(
                nameof(sensorManagementService));

        _analysisService =
            analysisService ??
            throw new ArgumentNullException(
                nameof(analysisService));

        _dashboardIssueService =
            dashboardIssueService ??
            throw new ArgumentNullException(
                nameof(dashboardIssueService));

        _dashboardEvents =
            dashboardEvents ??
            throw new ArgumentNullException(
                nameof(dashboardEvents));
    }

    /// <summary>
    /// Returns the existing Part 1 Adaptive Anomaly Spotlight snapshot.
    ///
    /// By default only unresolved issues are returned. Resolved episodes
    /// can be included for audit and operator investigation.
    ///
    /// Configuration reconciliation remains idempotent and does not
    /// increment occurrence counts on repeated unchanged GET requests.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DashboardSnapshotResponse>(
        StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSnapshotResponse>> Get(
        [FromQuery] bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        DashboardSnapshotResponse snapshot =
            await GetAuthoritativeSnapshotAsync(
                includeResolved,
                cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// P2-06: Returns an active-condition snapshot for the real-time client.
    ///
    /// GET /api/realtime/snapshot/active-conditions
    ///
    /// The absolute route is intentional: this action belongs to the
    /// existing dashboard controller because it shares the same
    /// authoritative lifecycle and configuration reconciliation.
    ///
    /// No second issue collection, second anomaly engine, extra SignalR
    /// hub, or new dependency-injection registration is introduced.
    ///
    /// The result includes both Active and Acknowledged conditions.
    /// Resolved conditions are absent and remain available through the
    /// original Part 1 historical snapshot.
    /// </summary>
    [HttpGet("/api/realtime/snapshot/active-conditions")]
    [ProducesResponseType<RealtimeActiveConditionsResponse>(
        StatusCodes.Status200OK)]
    public async Task<ActionResult<RealtimeActiveConditionsResponse>>
        GetRealtimeActiveConditions(
            CancellationToken cancellationToken)
    {
        DashboardSnapshotResponse dashboard =
            await GetAuthoritativeSnapshotAsync(
                includeResolved: false,
                cancellationToken);

        /*
         * DashboardIssueService.GetSnapshot(false) obtains its issues
         * from DashboardIssueCollection.GetActiveSnapshot().
         *
         * That collection method is governed by the actual HashSet of
         * unresolved sensor-plus-condition keys.
         *
         * The resulting DashboardIssueResponse objects are immutable,
         * so this projection cannot mutate Domain issue state.
         */
        RealtimeActiveConditionResponse[] conditions =
            dashboard.Issues
                .Where(
                    issue =>
                        issue.State !=
                        nameof(SmartX.Domain.Dashboard.DashboardIssueState.Resolved))
                .Select(ToActiveConditionResponse)
                .ToArray();

        /*
         * Calculate both counters from the same detached HTTP snapshot.
         *
         * This ensures the displayed counters always agree with the
         * returned condition rows, including during concurrent changes
         * made by incoming telemetry and sensor-status operations.
         */
        int activeCount = conditions.Length;

        int unacknowledgedCount =
            conditions.Count(
                condition =>
                    condition.State ==
                    nameof(SmartX.Domain.Dashboard.DashboardIssueState.Active));

        RealtimeActiveConditionsResponse response = new(
            dashboard.GeneratedAtUtc,
            activeCount,
            unacknowledgedCount,
            Array.AsReadOnly(conditions));

        return Ok(response);
    }

    /// <summary>
    /// Preserves the original Part 1 HTTP acknowledgement operation.
    ///
    /// The Domain issue transitions to Acknowledged but its condition
    /// remains in the HashSet until the underlying evidence recovers.
    ///
    /// The existing coordinator continues broadcasting dashboard and
    /// Part 2 real-time invalidations so clients refresh via HTTP.
    /// </summary>
    [HttpPost("{issueId:guid}/acknowledge")]
    [ProducesResponseType<DashboardIssueResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DashboardIssueResponse>> Acknowledge(
        Guid issueId,
        CancellationToken cancellationToken)
    {
        DashboardIssueResponse issue =
            _dashboardIssueService.Acknowledge(
                issueId);

        await _dashboardEvents.NotifyIssueAcknowledgedAsync(
            issue.SensorProfileId,
            cancellationToken);

        return Ok(issue);
    }

    /// <summary>
    /// Reuses the original Part 1 configuration reconciliation before
    /// creating either dashboard or real-time snapshots.
    ///
    /// Missing configuration can persist independently of process-local
    /// alert state. Both routes must therefore perform the same check
    /// instead of exposing different condition membership.
    ///
    /// Configuration analysis is idempotent when evidence is unchanged.
    /// Neither this method nor a snapshot GET processes telemetry again.
    /// </summary>
    private async Task<DashboardSnapshotResponse>
        GetAuthoritativeSnapshotAsync(
            bool includeResolved,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<SensorResponse> sensors =
            await _sensorManagementService.GetAllAsync(
                cancellationToken);

        foreach (SensorResponse sensor in sensors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _analysisService.AnalyzeConfigurationAsync(
                sensor.Id,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return _dashboardIssueService.GetSnapshot(
            includeResolved);
    }

    /// <summary>
    /// Maps an existing immutable Part 1 issue response into the narrower
    /// real-time membership contract.
    ///
    /// The stable membership key must match the canonical Domain rule:
    ///
    ///     sensor Guid (N format) + ":" + DashboardIssueType
    ///
    /// This key remains the same during recurrence. The IssueId changes
    /// after recovery because the new occurrence is a fresh issue episode.
    ///
    /// No new identifier or separate alert membership is generated here.
    /// </summary>
    private static RealtimeActiveConditionResponse
        ToActiveConditionResponse(
            DashboardIssueResponse issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        string membershipKey =
            $"{issue.SensorProfileId:N}:{issue.Type}";

        return new RealtimeActiveConditionResponse(
            membershipKey,
            issue.Id,
            issue.SensorProfileId,
            issue.Type,
            issue.Severity,
            issue.State,
            issue.OccurrenceCount,
            issue.PriorityScore,
            issue.FirstObservedAtUtc,
            issue.LastObservedAtUtc,
            issue.AcknowledgedAtUtc);
    }
}