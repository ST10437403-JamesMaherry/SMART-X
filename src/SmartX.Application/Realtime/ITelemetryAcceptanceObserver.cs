using SmartX.Application.Dashboard;

namespace SmartX.Application.Realtime;

/// <summary>
/// Completes synchronous Part 1 operational effects after persistence and
/// work admission. Implementations handle secondary notification failures.
/// There is intentionally no HTTP cancellation token after acceptance.
/// </summary>
public interface ITelemetryAcceptanceObserver
{
    Task ObserveAsync(Guid sensorProfileId, TelemetryAnomalyObservation observation);
}