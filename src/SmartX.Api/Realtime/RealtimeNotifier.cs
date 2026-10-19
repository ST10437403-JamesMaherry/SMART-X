using Microsoft.AspNetCore.SignalR;

using SmartX.Application.Realtime;
using SmartX.Contracts.Realtime;

namespace SmartX.Api.Realtime;

/// <summary>A disconnected/slow hub cannot fail acceptance or halt the worker.</summary>
public sealed class RealtimeNotifier(
    IHubContext<RealtimeHub> hub,
    TimeProvider timeProvider,
    ILogger<RealtimeNotifier> logger) : IRealtimeNotifier
{
    public async Task PublishAsync(string reason, Guid? sensorProfileId = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var notification = new RealtimeChangedResponse(reason, sensorProfileId,
                timeProvider.GetUtcNow().ToUniversalTime());
            await hub.Clients.All.SendAsync("RealtimeChanged", notification, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown may stop notifications, but never rolls back accepted work.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Real-time invalidation failed for {Reason}.", reason);
        }
    }
}