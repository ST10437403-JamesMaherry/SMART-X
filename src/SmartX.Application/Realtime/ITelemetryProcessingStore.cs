using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>Singleton operational state for the one hosted consumer.</summary>
public interface ITelemetryProcessingStore
{
    void MarkWaiting();
    void Begin(TelemetryDispatch dispatch, DateTimeOffset startedAtUtc);
    void Complete(TelemetryDispatch dispatch, DateTimeOffset processedAtUtc);
    void MarkFaulted(DateTimeOffset failedAtUtc);
    void MarkStopped(DateTimeOffset stoppedAtUtc);
    TelemetryDeviceProcessingState? GetDeviceState(Guid sensorProfileId);
    TelemetryProcessingSnapshot GetSnapshot();
}