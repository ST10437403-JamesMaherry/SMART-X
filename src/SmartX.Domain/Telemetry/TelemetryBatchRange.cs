namespace SmartX.Domain.Telemetry;

/// <summary>
/// Describes where one original raw telemetry batch is located inside
/// the flattened historical telemetry collection.
///
/// Keeping this metadata allows Smart-X to use an efficient sequential
/// List while still preserving the boundaries of the original batches.
/// </summary>
public readonly record struct TelemetryBatchRange
{
    internal TelemetryBatchRange(
        int batchIndex,
        int startIndex,
        int count)
    {
        BatchIndex = batchIndex;
        StartIndex = startIndex;
        Count = count;
    }

    /// <summary>
    /// Original position of the batch in the jagged array.
    /// </summary>
    public int BatchIndex { get; }

    /// <summary>
    /// Position where this batch begins in the flattened collection.
    /// </summary>
    public int StartIndex { get; }

    /// <summary>
    /// Number of telemetry packets contained in the batch.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// First index immediately after this batch.
    ///
    /// For example, a batch beginning at index 3 with 4 items has an
    /// EndExclusive value of 7.
    /// </summary>
    public int EndExclusive => StartIndex + Count;

    /// <summary>
    /// Indicates whether the original batch contained no telemetry packets.
    /// </summary>
    public bool IsEmpty => Count == 0;
}