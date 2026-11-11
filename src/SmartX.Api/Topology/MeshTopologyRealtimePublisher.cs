using SmartX.Application.Realtime;

namespace SmartX.Api.Topology;

/// <summary>
/// Publishes best-effort invalidations AFTER a successful simulation write.
///
/// RealtimeChanged conveys only the reason and time. It does not carry any
/// mutable graph, selected sensor, cached route, or fabricated graph version.
/// React clients MUST refetch the versioned HTTP topology, reachability,
/// MST and route projections after receiving an invalidation or reconnecting.
/// </summary>
public sealed class MeshTopologyRealtimePublisher
{
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<MeshTopologyRealtimePublisher> _logger;

    public MeshTopologyRealtimePublisher(
        IRealtimeNotifier notifier,
        ILogger<MeshTopologyRealtimePublisher> logger)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Never converts a persisted link operation into a failed HTTP response
    /// if a SignalR client disconnects or the transport is unavailable.
    /// The established notifier has its own bounded delivery timeout.
    /// Cancellation after the authoritative write is deliberately not tied
    /// to the original request's disconnect token.
    /// </summary>
    public async Task PublishSafelyAsync(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A topology invalidation reason is required.", nameof(reason));
        }

        try
        {
            await _notifier.PublishAsync(
                reason, sensorProfileId: null, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Topology invalidation delivery failed for {Reason}; " +
                "the committed graph mutation remains authoritative.", reason);
        }
    }
}