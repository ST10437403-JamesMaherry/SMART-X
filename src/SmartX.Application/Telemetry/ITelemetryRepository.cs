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
    /// Returns a bounded read-only snapshot; a cursor-capable HTTP facade
    /// will be added in the following P3-03 commit.
    /// </remarks>
    Task<IReadOnlyList<TelemetryPacket<T>>> GetRangeAsync<T>(
        Guid sensorProfileId,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toInclusiveUtc,
        int limit,
        CancellationToken cancellationToken = default)
        where T : struct;
}