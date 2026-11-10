namespace SmartX.Domain.Telemetry;

/// <summary>
/// One indexed packet and its zero-based position inside its UTC timestamp
/// bucket. The position is stable for accepted packets because duplicate
/// timestamps are only appended; existing packets are never reordered.
/// </summary>
public sealed record AvlRangeEntry<T>(
    TelemetryPacket<T> Packet,
    int TimestampSequence)
    where T : struct;

/// <summary>
/// A bounded, immutable-view result returned from one AVL range traversal.
/// HasMore is based on one additional indexed entry, not a full tree scan.
/// </summary>
public sealed record AvlRangePage<T>(
    IReadOnlyList<AvlRangeEntry<T>> Entries,
    bool HasMore)
    where T : struct;