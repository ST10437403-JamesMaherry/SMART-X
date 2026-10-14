using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>Resolved in a fresh operation scope for every dispatch.</summary>
public interface ITelemetryProcessor
{
    Task ProcessAsync(TelemetryDispatch dispatch, CancellationToken cancellationToken);
}