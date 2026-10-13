using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Realtime;

/// <summary>
/// Process-local admission and ordered dispatch, shared by ingestion and one
/// worker. Register the implementation as a singleton when wiring the worker.
/// </summary>
public interface ITelemetryWorkBuffer
{
    /// <summary>
    /// Admits an already accepted packet. No HTTP cancellation token is used:
    /// a disconnected caller must not revoke previously accepted work.
    /// Classification is supplied by the shared acceptance policy, not recalculated here.
    /// </summary>
    TelemetryWorkItem<T> Enqueue<T>(
        Guid sensorProfileId,
        TelemetryPacket<T> packet,
        DashboardIssueSeverity severity,
        DateTimeOffset acceptedAtUtc)
        where T : struct;

    /// <summary>
    /// Checks critical work first at every dispatch. Cancellation stops waiting
    /// without removing pending work. Dispatch cannot preempt executing work.
    /// </summary>
    ValueTask<TelemetryDispatch> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Simulation control: critical dispatch remains live while ordinary dispatch pauses.</summary>
    void SetOrdinaryDispatchPaused(bool paused);

    TelemetryWorkBufferSnapshot GetSnapshot();
}