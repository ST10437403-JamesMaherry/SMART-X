namespace SmartX.Domain.Realtime;

/// <summary>
/// Ordering of a bounded chronological operational history response.
/// Equal timestamps are ordered by the store-assigned insertion sequence.
/// </summary>
public enum HistorySortOrder
{
    Ascending = 1,
    Descending = 2
}

/// <summary>
/// A detached, read-only timeline response for one device.
///
/// The selected events are always the newest Limit retained events by
/// occurrence time, displayed in the requested chronological direction.
/// TotalRecordedEvents counts successful unique admissions, including entries
/// since removed by retention. It is not the Part 1 telemetry total.
/// </summary>
public sealed record RealtimeHistorySnapshot(
    Guid TargetDeviceId,
    long TotalRecordedEvents,
    int RetainedEventCount,
    int RetentionCapacity,
    int Limit,
    HistorySortOrder Order,
    IReadOnlyList<HistoryEntry> Events);