using System.ComponentModel.DataAnnotations;

using SmartX.Contracts.Telemetry;

namespace SmartX.Contracts.Realtime;

/// <summary>Actual dispatch/completion evidence in dequeue sequence order.</summary>
public sealed record TelemetryDispatchResponse(
    long EnqueueSequence,
    long DequeueSequence,
    string Lane,
    string Severity,
    TelemetryReadingResponse Telemetry,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Outcome,
    bool UpdatedLatestObservation);

/// <summary>
/// AcceptedCount = PendingCount + ProcessedCount + OutstandingDispatchedCount.
/// Outstanding includes executing, failed or interrupted retained work.
/// RecentDispatches contains at most 100 dispatches; totals cover the process lifetime.
/// </summary>
public sealed record TelemetryProcessingResponse(
    long AcceptedCount,
    long DispatchedCount,
    long ProcessedCount,
    int OrdinaryPendingCount,
    int CriticalPendingCount,
    int PendingCount,
    long OutstandingDispatchedCount,
    bool OrdinaryDispatchPaused,
    string WorkerStatus,
    string? Failure,
    TelemetryDispatchResponse? CurrentDispatch,
    IReadOnlyList<TelemetryDispatchResponse> RecentDispatches);

/// <summary>Simulation only: pause pending ordinary dispatch while critical work stays live.</summary>
public sealed record SetOrdinaryDispatchRequest([Required] bool? Paused);

public sealed record RealtimeChangedResponse(
    string Reason,
    Guid? SensorProfileId,
    DateTimeOffset OccurredAtUtc);