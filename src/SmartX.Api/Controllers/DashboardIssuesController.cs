using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
using SmartX.Application.Dashboard;
using SmartX.Application.Sensors;
using SmartX.Contracts.Dashboard;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes the server-authoritative Adaptive Anomaly Spotlight state.
///
/// SignalR informs clients that dashboard state may have changed, while this
/// controller remains the authoritative source from which the React client
/// retrieves the complete prioritized issue snapshot.
/// </summary>
[ApiController]
[Route("api/dashboard/issues")]
public sealed class DashboardIssuesController
    : ControllerBase
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
    /// Returns the current Adaptive Anomaly Spotlight snapshot.
    ///
    /// Configuration evidence is re-evaluated before the snapshot is created.
    /// The operation is idempotent: repeatedly reading an unchanged missing
    /// configuration state does not increase its occurrence count.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DashboardSnapshotResponse>(
        StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSnapshotResponse>> Get(
        [FromQuery] bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SmartX.Contracts.Sensors.SensorResponse> sensors =
            await _sensorManagementService.GetAllAsync(
                cancellationToken);

        /*
         * Configuration attachments can persist independently from the
         * process-local dashboard collection. Re-evaluating registered
         * sensors ensures the returned dashboard always reflects the current
         * attachment evidence.
         */
        foreach (SmartX.Contracts.Sensors.SensorResponse sensor in sensors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _analysisService
                .AnalyzeConfigurationAsync(
                    sensor.Id,
                    cancellationToken);
        }

        DashboardSnapshotResponse snapshot =
            _dashboardIssueService.GetSnapshot(
                includeResolved);

        return Ok(
            snapshot);
    }

    /// <summary>
    /// Acknowledges one dashboard issue without resolving its underlying
    /// operational condition.
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

        await _dashboardEvents
            .NotifyIssueAcknowledgedAsync(
                issue.SensorProfileId,
                cancellationToken);

        return Ok(
            issue);
    }
}