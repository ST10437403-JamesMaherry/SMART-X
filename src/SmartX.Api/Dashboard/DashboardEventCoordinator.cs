using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Api.Dashboard;

/// <summary>
/// Coordinates dashboard analysis after successful API operations.
///
/// Controllers remain responsible for HTTP concerns and their primary
/// business operation. This coordinator performs the secondary Adaptive
/// Anomaly Spotlight analysis and publishes real-time invalidation events.
///
/// Analysis and notification occur only after the primary operation has
/// succeeded. A secondary dashboard failure is logged instead of turning an
/// already persisted telemetry packet or attachment into a misleading failed
/// HTTP operation.
/// </summary>
public sealed class DashboardEventCoordinator
{
    private readonly AdaptiveAnomalyAnalysisService _analysisService;
    private readonly SensorManagementService _sensorManagementService;
    private readonly DashboardRealtimeNotifier _notifier;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<DashboardEventCoordinator> _logger;

    public DashboardEventCoordinator(
        AdaptiveAnomalyAnalysisService analysisService,
        SensorManagementService sensorManagementService,
        DashboardRealtimeNotifier notifier,
        ILogger<DashboardEventCoordinator> logger,
        IRealtimeNotifier realtimeNotifier)
    {
        _realtimeNotifier = realtimeNotifier;

        _analysisService =
            analysisService ??
            throw new ArgumentNullException(
                nameof(analysisService));

        _sensorManagementService =
            sensorManagementService ??
            throw new ArgumentNullException(
                nameof(sensorManagementService));

        _notifier =
            notifier ??
            throw new ArgumentNullException(
                nameof(notifier));

        _logger =
            logger ??
            throw new ArgumentNullException(
                nameof(logger));
    }

    /// <summary>
    /// Publishes the fact that the registered-sensor collection changed.
    ///
    /// Missing configuration is evaluated when the dashboard snapshot is
    /// requested, avoiding unnecessary attachment infrastructure resolution
    /// during ordinary sensor registration.
    /// </summary>
    public Task NotifySensorRegisteredAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        return PublishSafelyAsync(
            DashboardChangeReasons.SensorRegistered,
            sensorProfileId,
            cancellationToken);
    }

    /// <summary>Invalidates device metadata after a successful profile edit.</summary>
    public Task NotifySensorUpdatedAsync(Guid sensorProfileId)
        => PublishSafelyAsync("sensor-updated", sensorProfileId, CancellationToken.None);

    /// <summary>
    /// Reacts to successfully persisted telemetry.
    ///
    /// A previously Offline sensor is first promoted to Online because valid
    /// telemetry proves gateway connectivity. The exact classification captured at acceptance
    /// is then applied once, independently of HTTP request cancellation.
    /// </summary>
    public async Task AnalyzeAcceptedTelemetryAsync(
        Guid sensorProfileId,
        TelemetryAnomalyObservation observation)
    {
        bool becameOnline = false;

        await ExecuteAndPublishAsync(
            sensorProfileId,
            DashboardChangeReasons.TelemetryChanged,
            async token =>
            {
                SensorStatusChangeResult? stateChange =
                    await _sensorManagementService
                        .MarkOnlineFromTelemetryAsync(
                            sensorProfileId,
                            token);

                if (stateChange is
                    {
                        Changed: true
                    })
                {
                    // The existing telemetry observer can change forwarding
                    // eligibility without a PATCH /api/sensors/{id}/status.
                    // Publish a topology-relevant status reason only for
                    // this actual transition, never for every telemetry packet.
                    becameOnline = true;

                    await _analysisService
                        .AnalyzeSensorStateAsync(
                            sensorProfileId,
                            stateChange.PreviousStatus,
                            token);
                }

                await _analysisService
                    .AnalyzeAcceptedTelemetryAsync(
                        sensorProfileId,
                        observation,
                        token);
            },
            CancellationToken.None);

        if (becameOnline)
        {
            await PublishSafelyAsync(
                DashboardChangeReasons.SensorStatusChanged,
                sensorProfileId,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Reacts to an explicit status transition that has already been
    /// successfully persisted.
    /// </summary>
    public Task AnalyzeSensorStatusAsync(
        Guid sensorProfileId,
        SensorStatus previousStatus,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAndPublishAsync(
            sensorProfileId,
            DashboardChangeReasons.SensorStatusChanged,
            token =>
                _analysisService
                    .AnalyzeSensorStateAsync(
                        sensorProfileId,
                        previousStatus,
                        token),
            cancellationToken);
    }

    /// <summary>
    /// Re-evaluates the configuration issue after a configuration attachment
    /// changes.
    /// </summary>
    public Task AnalyzeConfigurationAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAndPublishAsync(
            sensorProfileId,
            DashboardChangeReasons.ConfigurationChanged,
            token =>
                _analysisService
                    .AnalyzeConfigurationAsync(
                        sensorProfileId,
                        token),
            cancellationToken);
    }

    /// <summary>
    /// Publishes acknowledgement changes so every open dashboard can refresh
    /// its issue ranking immediately.
    /// </summary>
    public Task NotifyIssueAcknowledgedAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        return PublishSafelyAsync(
            DashboardChangeReasons.IssueAcknowledged,
            sensorProfileId,
            cancellationToken);
    }

    /// <summary>
    /// Runs secondary dashboard analysis without compromising the successful
    /// primary API operation when analysis itself unexpectedly fails.
    /// </summary>
    private async Task ExecuteAndPublishAsync(
        Guid sensorProfileId,
        string reason,
        Func<CancellationToken, Task> analysis,
        CancellationToken cancellationToken)
    {
        try
        {
            await analysis(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            /*
             * The primary operation has already succeeded. Log the dashboard
             * failure rather than returning a false failed response for data
             * that was successfully persisted.
             */
            _logger.LogError(
                exception,
                "Adaptive dashboard analysis failed for sensor {SensorId}.",
                sensorProfileId);
        }

        await PublishSafelyAsync(
            reason,
            sensorProfileId,
            cancellationToken);
    }

    /// <summary>
    /// Treats SignalR as a best-effort notification channel.
    ///
    /// Clients also load snapshots through HTTP, so a temporary hub failure
    /// must never corrupt or roll back the underlying sensor operation.
    /// </summary>
    private async Task PublishSafelyAsync(
        string reason,
        Guid? sensorProfileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.PublishAsync(
                reason,
                sensorProfileId,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Dashboard SignalR notification failed for reason {Reason}.",
                reason);
        }

        // Part 2 device/processing reads also change on these operations.
        // Keep this independent of the Part 1 hub's delivery result.
        try
        {
            await _realtimeNotifier.PublishAsync(reason, sensorProfileId, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Real-time invalidation failed for {Reason}.", reason);
        }

    }
}