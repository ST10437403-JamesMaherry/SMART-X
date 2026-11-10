using SmartX.Domain.Telemetry;

namespace SmartX.Application.Telemetry;

/// <summary>
/// Defines the strongly typed persistence operations required by Smart-X
/// telemetry use cases.
///
/// Generic methods keep telemetry payloads in their native value type across
/// the Application/Infrastructure boundary. Implementations must therefore
/// never need to weaken telemetry packets to object or dynamic values.
/// </summary>
public interface ITelemetryRepository
{
    /// <summary>
    /// Stores one strongly typed telemetry packet for a registered sensor.
    /// </summary>
    Task AddAsync<T>(
        Guid sensorProfileId,
        TelemetryPacket<T> packet,
        CancellationToken cancellationToken = default)
        where T : struct;

    /// <summary>
    /// Stores an already validated historical telemetry collection.
    ///
    /// HistoricalTelemetryBuffer preserves the sequence and original batch
    /// boundaries established from the raw jagged input.
    /// Implementations must commit the complete batch atomically: cancellation
    /// or a validation failure must not leave a partially persisted batch.
    /// Successful return is the acceptance boundary for queued processing.
    /// </summary>
    Task AddBatchAsync<T>(
        Guid sensorProfileId,
        HistoricalTelemetryBuffer<T> history,
        CancellationToken cancellationToken = default)
        where T : struct;

    /// <summary>
    /// Returns up to the requested number of most recently stored telemetry
    /// packets while preserving their original ingestion order.
    /// </summary>
    Task<IReadOnlyList<TelemetryPacket<T>>> GetRecentAsync<T>(
        Guid sensorProfileId,
        int limit,
        CancellationToken cancellationToken = default)
        where T : struct;

    /// <summary>
    /// Retrieves accepted packets in ascending capture-time order from the
    /// AVL secondary index, retaining ingestion order within a UTC timestamp.
    /// This does not change GetRecentAsync's append-order contract.
    /// </summary>
    /// <remarks>
    /// Both history views contain the same immutable packet instances.
    /// Returns a bounded read-only snapshot. GetRangePageAsync provides
    /// a composite cursor for equal-timestamp pagination.
    /// </remarks>
    Task<IReadOnlyList<TelemetryPacket<T>>> GetRangeAsync<T>(
        Guid sensorProfileId,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toInclusiveUtc,
        int limit,
        CancellationToken cancellationToken = default)
        where T : struct;

    /// <summary>
    /// Retrieves a bounded AVL page with an optional composite continuation.
    /// Each returned entry includes its zero-based sequence within the
    /// timestamp bucket; a continuation must supply both cursor components.
    /// </summary>
    Task<AvlRangePage<T>> GetRangePageAsync<T>(
        Guid sensorProfileId,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toInclusiveUtc,
        int limit,
        DateTimeOffset? afterTimestampUtc = null,
        int? afterTimestampSequence = null,
        CancellationToken cancellationToken = default)
        where T : struct;

}