namespace SmartX.Application.Realtime;

/// <summary>Best-effort invalidations; HTTP snapshots remain authoritative.</summary>
public interface IRealtimeNotifier
{
    Task PublishAsync(string reason, Guid? sensorProfileId = null,
        CancellationToken cancellationToken = default);
}