using SmartX.Application.Sensors;
using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>
/// Updates operational observations from the exact accepted work item. Typed
/// persistence, Online transitions and Part 1 anomaly recovery already ran at
/// acceptance; replaying those effects here would double-count or reorder them.
/// Future history/recommendation work attaches to this scoped processing path.
/// </summary>
public sealed class TelemetryProcessingService(
    ISensorRepository sensors,
    ITelemetryProcessingStore state,
    TimeProvider timeProvider) : ITelemetryProcessor
{
    public async Task ProcessAsync(TelemetryDispatch dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var item = dispatch.WorkItem;
        var sensor = await sensors.GetByIdAsync(item.SensorProfileId, cancellationToken)
            ?? throw new SensorNotFoundException(item.SensorProfileId);
        if (sensor.DeviceIdentifier != item.SourceDeviceIdentifier)
        {
            throw new InvalidOperationException("Accepted telemetry no longer matches its registered device.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Commit observation and processed metrics together. Metadata/status
        // are still owned by the canonical sensor registry and are not replayed.
        state.Complete(dispatch, timeProvider.GetUtcNow());
    }
}