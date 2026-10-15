using SmartX.Contracts.Commands;
using SmartX.Contracts.Telemetry;

namespace SmartX.Contracts.Realtime;

/// <summary>
/// One event in a registered device's chronological operational timeline.
///
/// OccurredAtUtc determines chronological placement.
/// RecordedAtUtc records when processing completed or the command executed.
/// Sequence breaks equal-timestamp ties deterministically.
///
/// Exactly one of Telemetry or Command is populated for supported events.
/// </summary>
public sealed record RealtimeHistoryEventResponse(
    string EventId,
    Guid TargetDeviceId,
    string Kind,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    long Sequence,
    TelemetryReadingResponse? Telemetry,
    CommandAuditResponse? Command,
    long? EnqueueSequence,
    long? DequeueSequence,
    string? ProcessingLane,
    string? Severity,
    DateTimeOffset? AcceptedAtUtc);

/// <summary>
/// Bounded operational history for one registered Smart-X device.
///
/// Events contains the newest Limit retained events, displayed in the
/// requested chronological order.
///
/// TotalRecordedEvents counts unique admissions over the process lifetime.
/// RetainedEventCount is the number still held by the independent
/// operational-history retention policy.
/// </summary>
public sealed record RealtimeHistoryResponse(
    Guid TargetDeviceId,
    long TotalRecordedEvents,
    int RetainedEventCount,
    int RetentionCapacity,
    int Limit,
    string Order,
    IReadOnlyList<RealtimeHistoryEventResponse> Events);