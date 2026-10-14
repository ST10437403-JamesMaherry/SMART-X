using SmartX.Application.Dashboard;
using SmartX.Application.Telemetry;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Realtime;

/// <summary>
/// Publishes exact validated packets only after typed persistence succeeds.
/// Captures predecessor/classification before another request for the same
/// sensor can alter the series. Does not reconstruct batches from recent history.
/// </summary>
public sealed class TelemetryAcceptanceService(
    ITelemetryRepository repository,
    ITelemetryWorkBuffer buffer,
    TelemetryIngestionGate ingestionGate,
    TelemetryAnomalyClassifier classifier,
    ITelemetryAcceptanceObserver observer,
    TimeProvider timeProvider)
{
    public Task AcceptAsync<T>(SensorProfile sensor, TelemetryPacket<T> packet,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        return PersistAndPublishAsync(sensor, new[] { packet },
            token => repository.AddAsync(sensor.Id, packet, token), cancellationToken);
    }

    public Task AcceptBatchAsync<T>(SensorProfile sensor, HistoricalTelemetryBuffer<T> history,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        return PersistAndPublishAsync(sensor, history,
            token => repository.AddBatchAsync(sensor.Id, history, token), cancellationToken);
    }

    private async Task PersistAndPublishAsync<T>(SensorProfile sensor,
        IReadOnlyList<TelemetryPacket<T>> packets, Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
        where T : struct
    {
        using var lease = await ingestionGate.EnterAsync(sensor.Id, cancellationToken);

        // Only one predecessor is fetched; the supplied collection is the
        // complete accepted batch, even when it contains more than 100 packets.
        var recent = await repository.GetRecentAsync<T>(sensor.Id, 1, cancellationToken);
        TelemetryPacket<T>? previous = recent.Count == 0 ? null : recent[^1];
        var observations = new TelemetryAnomalyObservation[packets.Count];
        for (var index = 0; index < packets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations[index] = sensor.Status == SensorStatus.Decommissioned
                ? TelemetryAnomalyObservation.None
                : classifier.Classify(previous, packets[index]);
            previous = packets[index];
        }

        // The repository checks cancellation before its atomic write. Once it
        // succeeds, HTTP cancellation cannot revoke admission or operational effects.
        cancellationToken.ThrowIfCancellationRequested();
        await persist(cancellationToken);
        var acceptedAtUtc = timeProvider.GetUtcNow();
        for (var index = 0; index < packets.Count; index++)
        {
            buffer.Enqueue(sensor.Id, packets[index], observations[index].Severity, acceptedAtUtc);
        }

        if (packets.Count > 0)
        {
            // Preserve Part 1's one analysis per successful request: for a
            // batch, only its final pair updates occurrence/recovery counts.
            await observer.ObserveAsync(sensor.Id, observations[^1]);
        }
    }
}