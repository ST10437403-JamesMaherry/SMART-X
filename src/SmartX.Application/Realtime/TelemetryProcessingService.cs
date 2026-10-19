using SmartX.Application.Sensors;
using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>
/// Updates operational observations from exact accepted telemetry work.
///
/// Typed persistence, Online transitions, and Part 1 anomaly processing
/// already occur at acceptance. This service must never replay them.
///
/// A successful dispatch also produces one source-linked chronological
/// history event, using its original capture timestamp.
/// </summary>
public sealed class TelemetryProcessingService(
    ISensorRepository sensors,
    ITelemetryProcessingStore state,
    TimeProvider timeProvider,
    OperationalHistoryRecorder? historyRecorder = null)
    : ITelemetryProcessor
{
    /// <summary>
    /// Completes one real worker dispatch and records its outcome.
    ///
    /// Accepted work retains its native typed packet; no capped telemetry
    /// history endpoint is queried or used to reconstruct the payload.
    /// </summary>
    public async Task ProcessAsync(
        TelemetryDispatch dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        TelemetryWorkItem item = dispatch.WorkItem;

        // Retain P2-03's authoritative registration and identifier checks.
        var sensor =
            await sensors.GetByIdAsync(
                item.SensorProfileId,
                cancellationToken)
            ?? throw new SensorNotFoundException(item.SensorProfileId);

        if (sensor.DeviceIdentifier != item.SourceDeviceIdentifier)
        {
            throw new InvalidOperationException(
                "Accepted telemetry no longer matches its " +
                "registered device.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The processing store owns the actual completed dispatch,
        // operational observation, and existing accounting invariants.
        //
        // Capture completion time once so processing diagnostics and the
        // chronological event use the same processing instant.
        DateTimeOffset processedAtUtc =
            timeProvider.GetUtcNow();

        state.Complete(dispatch, processedAtUtc);

        // Only successfully completed telemetry becomes operational history.
        //
        // A secondary history projection failure must not invalidate the
        // processing state that has already been committed.
        //
        // The recorder's stable source identity prevents duplicate rows.
        historyRecorder?.RecordProcessedTelemetry(
            dispatch,
            processedAtUtc);
    }
}