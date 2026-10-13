namespace SmartX.Domain.Realtime;

public enum TelemetryWorkerStatus
{
    Starting,
    Waiting,
    Processing,
    Faulted,
    Stopped
}

public enum TelemetryDispatchOutcome
{
    Processing,
    Processed,
    Failed,
    Interrupted
}

/// <summary>Actual dispatch evidence, not a sorted preview of the queues.</summary>
public sealed record TelemetryProcessingRecord(
    TelemetryDispatch Dispatch,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    TelemetryDispatchOutcome Outcome,
    bool UpdatedLatestObservation);

/// <summary>The newest observation and the time any packet last completed for this device.</summary>
public sealed record TelemetryDeviceProcessingState(
    TelemetryWorkItem LatestObservation,
    DateTimeOffset LastProcessedAtUtc);

/// <summary>
/// Detached process-local diagnostics. CurrentDispatch retains the exact typed
/// packet on failure or interrupted shutdown. RecentDispatches is bounded;
/// lifetime counts and pending/retained work are never truncated by that limit.
/// </summary>
public sealed record TelemetryProcessingSnapshot(
    TelemetryWorkBufferSnapshot Buffer,
    long ProcessedCount,
    TelemetryWorkerStatus WorkerStatus,
    TelemetryDispatch? CurrentDispatch,
    string? Failure,
    IReadOnlyList<TelemetryProcessingRecord> RecentDispatches)
{
    // Dequeue and Begin are separate short operations. Derive this count from
    // totals so even a snapshot between them accounts for every accepted packet.
    public long OutstandingDispatchedCount => Buffer.DispatchedCount - ProcessedCount;
}