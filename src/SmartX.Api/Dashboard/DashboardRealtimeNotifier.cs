using Microsoft.AspNetCore.SignalR;

using SmartX.Contracts.Dashboard;

namespace SmartX.Api.Dashboard;

/// <summary>
/// Publishes lightweight real-time dashboard change notifications through
/// SignalR.
///
/// Dashboard issue state remains authoritative on the server and is retrieved
/// through the HTTP snapshot endpoint after a notification is received.
/// </summary>
public sealed class DashboardRealtimeNotifier
{
    private readonly IHubContext<
        DashboardHub,
        IDashboardClient> _hubContext;

    private readonly TimeProvider _timeProvider;

    public DashboardRealtimeNotifier(
        IHubContext<
            DashboardHub,
            IDashboardClient> hubContext,
        TimeProvider timeProvider)
    {
        _hubContext =
            hubContext ??
            throw new ArgumentNullException(
                nameof(hubContext));

        _timeProvider =
            timeProvider ??
            throw new ArgumentNullException(
                nameof(timeProvider));
    }

    /// <summary>
    /// Broadcasts a dashboard invalidation event to every currently connected
    /// operator client.
    /// </summary>
    public async Task PublishAsync(
        string reason,
        Guid? sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(
                reason))
        {
            throw new ArgumentException(
                "A dashboard change reason is required.",
                nameof(reason));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var notification =
            new DashboardChangedResponse(
                reason.Trim(),
                sensorProfileId,
                _timeProvider
                    .GetUtcNow()
                    .ToUniversalTime());

        await _hubContext
            .Clients
            .All
            .DashboardChanged(
                notification);

        cancellationToken.ThrowIfCancellationRequested();
    }
}