using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>
/// Process-local, per-device chronological operational event storage.
///
/// A real SortedDictionary indexes each device's occurrence timestamps.
/// Equal timestamps retain multiple events with a deterministic sequence.
/// Part 1 telemetry persistence and the P2-03 processing diagnostics remain
/// separate from this operational history abstraction.
/// </summary>
public interface IRealtimeHistoryStore
{
    public const int DefaultQueryLimit = 100;
    public const int MaximumQueryLimit = 500;

    /// <summary>
    /// Inserts a unique, unsequenced event and assigns its insertion order.
    /// Returns false for a repeated identical source event still retained in
    /// history; a conflicting reuse of that identity raises an exception.
    /// </summary>
    bool TryAppend(HistoryEntry entry);

    /// <summary>
    /// Returns the newest bounded subset of retained events in the explicit
    /// requested order. Invalid limits and unknown sort values are rejected.
    /// An unknown device has an empty timeline (API registration validation
    /// will be provided separately in Commit 2).
    /// </summary>
    RealtimeHistorySnapshot GetSnapshot(
        Guid targetDeviceId,
        int limit = DefaultQueryLimit,
        HistorySortOrder order = HistorySortOrder.Ascending);
}