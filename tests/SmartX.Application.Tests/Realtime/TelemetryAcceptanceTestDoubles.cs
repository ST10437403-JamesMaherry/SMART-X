using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Realtime;

/// <summary>Records acceptance while keeping Application tests independent of Infrastructure.</summary>
internal sealed class RecordingTelemetryWorkBuffer : ITelemetryWorkBuffer
{
    public List<TelemetryWorkItem> Items { get; } = [];

    public TelemetryWorkItem<T> Enqueue<T>(Guid sensorProfileId, TelemetryPacket<T> packet,
        DashboardIssueSeverity severity, DateTimeOffset acceptedAtUtc) where T : struct
    {
        var item = new TelemetryWorkItem<T>(sensorProfileId, packet, severity, acceptedAtUtc, Items.Count + 1);
        Items.Add(item);
        return item;
    }

    public ValueTask<TelemetryDispatch> DequeueAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("These tests observe admission only.");

    public void SetOrdinaryDispatchPaused(bool paused)
        => throw new NotSupportedException("These tests observe admission only.");

    public TelemetryWorkBufferSnapshot GetSnapshot() => new(
        Items.Count, 0, false,
        Items.Where(item => item.Lane == TelemetryProcessingLane.Ordinary).ToArray(),
        Items.Where(item => item.Lane == TelemetryProcessingLane.Critical).ToArray());
}

internal sealed class RecordingTelemetryAcceptanceObserver : ITelemetryAcceptanceObserver
{
    public List<TelemetryAnomalyObservation> Observations { get; } = [];

    public Task ObserveAsync(Guid sensorProfileId, TelemetryAnomalyObservation observation)
    {
        Observations.Add(observation);
        return Task.CompletedTask;
    }
}