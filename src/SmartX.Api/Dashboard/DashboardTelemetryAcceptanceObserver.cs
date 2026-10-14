using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;

namespace SmartX.Api.Dashboard;

/// <summary>Bridges accepted Application work to the existing best-effort dashboard coordinator.</summary>
public sealed class DashboardTelemetryAcceptanceObserver(DashboardEventCoordinator coordinator)
    : ITelemetryAcceptanceObserver
{
    public Task ObserveAsync(Guid sensorProfileId, TelemetryAnomalyObservation observation)
    {
        return coordinator.AnalyzeAcceptedTelemetryAsync(sensorProfileId, observation);
    }
}