using System.Collections;

using SmartX.Domain.Exceptions;

namespace SmartX.Domain.Telemetry;

/// <summary>
/// Represents historical Smart-X telemetry after raw jagged batches have
/// been transferred into an optimised sequential collection.
///
/// The original input uses a jagged array because telemetry batches may
/// contain different numbers of readings. Internally, packets are stored
/// in a pre-sized List to provide efficient sequential access while batch
/// ranges preserve the structure of the original array.
///
/// The collection is exposed through IReadOnlyList so callers can inspect
/// historical telemetry without mutating previously captured readings.
/// </summary>
/// <typeparam name="T">
/// Strongly typed telemetry payload such as double, int or bool.
/// </typeparam>
public sealed class HistoricalTelemetryBuffer<T>
    : IReadOnlyList<TelemetryPacket<T>>
    where T : struct
{
    private readonly List<TelemetryPacket<T>> _packets;
    private readonly List<TelemetryBatchRange> _batchRanges;

    private HistoricalTelemetryBuffer(
        List<TelemetryPacket<T>> packets,
        List<TelemetryBatchRange> batchRanges)
    {
        _packets = packets;
        _batchRanges = batchRanges;
    }

    /// <summary>
    /// Total number of telemetry packets in the flattened history.
    /// </summary>
    public int Count => _packets.Count;

    /// <summary>
    /// Number of original batches supplied to the processor.
    ///
    /// Empty batches are included because they still formed part of the
    /// original sequential input.
    /// </summary>
    public int BatchCount => _batchRanges.Count;

    /// <summary>
    /// Retrieves a telemetry packet using its flattened sequence position.
    /// </summary>
    public TelemetryPacket<T> this[int index] => _packets[index];

    /// <summary>
    /// Converts raw jagged telemetry batches into an optimised historical
    /// collection while preserving exact batch and packet sequence order.
    ///
    /// No sorting occurs during conversion. The original order supplied by
    /// the ingestion process is deliberately retained.
    /// </summary>
    /// <param name="rawBatches">
    /// Jagged array containing sequential telemetry batches.
    /// </param>
    /// <returns>
    /// Read-only historical telemetry buffer backed by pre-sized Lists.
    /// </returns>
    /// <exception cref="InvalidTelemetryBatchException">
    /// Thrown when the outer array, an inner batch, or a telemetry packet
    /// contains an invalid null value.
    /// </exception>
    public static HistoricalTelemetryBuffer<T> FromJaggedBatches(
        TelemetryPacket<T>[][]? rawBatches)
    {
        if (rawBatches is null)
        {
            throw new InvalidTelemetryBatchException(
                "Historical telemetry batches cannot be null.");
        }

        // Perform validation before constructing the result so invalid input
        // cannot leave us with a partially populated history.
        var totalPacketCount = ValidateAndCountPackets(rawBatches);

        // Pre-sizing prevents repeated List capacity growth while transferring
        // potentially large historical telemetry batches.
        var packets =
            new List<TelemetryPacket<T>>(totalPacketCount);

        var batchRanges =
            new List<TelemetryBatchRange>(rawBatches.Length);

        for (var batchIndex = 0;
             batchIndex < rawBatches.Length;
             batchIndex++)
        {
            var batch = rawBatches[batchIndex];

            // The first validation pass guarantees this value is non-null.
            // This guard keeps the invariant explicit for maintainability.
            if (batch is null)
            {
                throw new InvalidTelemetryBatchException(
                    $"Telemetry batch {batchIndex} cannot be null.",
                    batchIndex);
            }

            var startIndex = packets.Count;

            // AddRange preserves the existing sequence order of every packet
            // and is clearer than repeatedly inserting individual elements.
            packets.AddRange(batch);

            batchRanges.Add(
                new TelemetryBatchRange(
                    batchIndex,
                    startIndex,
                    batch.Length));
        }

        return new HistoricalTelemetryBuffer<T>(
            packets,
            batchRanges);
    }

    /// <summary>
    /// Returns the location of an original batch inside the flattened
    /// telemetry collection.
    /// </summary>
    public TelemetryBatchRange GetBatchRange(int batchIndex)
    {
        if (batchIndex < 0 || batchIndex >= _batchRanges.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchIndex),
                batchIndex,
                "The requested telemetry batch does not exist.");
        }

        return _batchRanges[batchIndex];
    }

    /// <summary>
    /// Enumerates only the telemetry packets belonging to one original batch.
    ///
    /// The packets are yielded directly from the underlying List instead of
    /// allocating another temporary collection.
    /// </summary>
    public IEnumerable<TelemetryPacket<T>> EnumerateBatch(
        int batchIndex)
    {
        var range = GetBatchRange(batchIndex);

        for (var index = range.StartIndex;
             index < range.EndExclusive;
             index++)
        {
            yield return _packets[index];
        }
    }

    /// <summary>
    /// Returns a generic enumerator over the flattened telemetry history.
    /// </summary>
    public IEnumerator<TelemetryPacket<T>> GetEnumerator()
    {
        return _packets.GetEnumerator();
    }

    /// <summary>
    /// Returns the non-generic enumerator required by IEnumerable.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private static int ValidateAndCountPackets(
        TelemetryPacket<T>[][] rawBatches)
    {
        long totalPacketCount = 0;

        for (var batchIndex = 0;
             batchIndex < rawBatches.Length;
             batchIndex++)
        {
            var batch = rawBatches[batchIndex];

            if (batch is null)
            {
                throw new InvalidTelemetryBatchException(
                    $"Telemetry batch {batchIndex} cannot be null.",
                    batchIndex);
            }

            for (var sequenceIndex = 0;
                 sequenceIndex < batch.Length;
                 sequenceIndex++)
            {
                if (batch[sequenceIndex] is null)
                {
                    throw new InvalidTelemetryBatchException(
                        $"Telemetry packet at batch {batchIndex}, " +
                        $"sequence {sequenceIndex} cannot be null.",
                        batchIndex,
                        sequenceIndex);
                }
            }

            totalPacketCount += batch.Length;

            // List<T> uses an Int32-based capacity. Rejecting impossible
            // collection sizes here produces a domain-specific error rather
            // than allowing an obscure capacity failure later.
            if (totalPacketCount > int.MaxValue)
            {
                throw new InvalidTelemetryBatchException(
                    "Historical telemetry exceeds the supported " +
                    "collection size.");
            }
        }

        return (int)totalPacketCount;
    }
}