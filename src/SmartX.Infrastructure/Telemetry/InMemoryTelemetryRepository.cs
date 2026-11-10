using System.Collections.Concurrent;

using SmartX.Application.Telemetry;
using SmartX.Domain.Telemetry;

namespace SmartX.Infrastructure.Telemetry;

/// <summary>
/// Provides thread-safe, process-local storage for Smart-X telemetry.
///
/// Part 1 deliberately uses in-memory persistence so that the telemetry
/// architecture can be demonstrated without introducing an unnecessary
/// external database dependency.
///
/// Each sensor and payload type receives an independent strongly typed
/// telemetry series. This allows Smart-X to retain compile-time telemetry
/// types while still supporting multiple sensor configurations.
/// </summary>
public sealed class InMemoryTelemetryRepository
    : ITelemetryRepository
{
    private readonly ConcurrentDictionary<
        (Guid SensorId, Type PayloadType),
        object> _stores = new();

    /// <summary>
    /// Stores one accepted packet in both the authoritative ingestion List
    /// and its AVL secondary index in a single per-series write boundary.
    /// </summary>
    public Task AddAsync<T>(
        Guid sensorProfileId,
        TelemetryPacket<T> packet,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        ValidateSensorProfileId(sensorProfileId);

        ArgumentNullException.ThrowIfNull(packet);

        cancellationToken.ThrowIfCancellationRequested();

        var store =
            GetOrCreateStore<T>(
                sensorProfileId);

        store.Add(packet, cancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stores the flattened packet sequence contained in a validated
    /// HistoricalTelemetryBuffer.
    ///
    /// The buffer has already preserved and validated the original jagged
    /// batch ordering before reaching Infrastructure.
    /// </summary>
    public Task AddBatchAsync<T>(
        Guid sensorProfileId,
        HistoricalTelemetryBuffer<T> history,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        ValidateSensorProfileId(sensorProfileId);

        ArgumentNullException.ThrowIfNull(history);

        cancellationToken.ThrowIfCancellationRequested();

        var store =
            GetOrCreateStore<T>(
                sensorProfileId);

        store.AddRange(
            history,
            cancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns up to the requested number of most recently ingested packets
    /// while preserving their original ingestion order.
    ///
    /// For example, if packets 1, 2 and 3 exist and limit is 2, the result
    /// contains packets 2 and 3 in that order.
    /// </summary>
    public Task<IReadOnlyList<TelemetryPacket<T>>> GetRecentAsync<T>(
        Guid sensorProfileId,
        int limit,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        ValidateSensorProfileId(sensorProfileId);

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "The telemetry limit must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var key =
            CreateStoreKey<T>(
                sensorProfileId);

        if (!_stores.TryGetValue(
                key,
                out var stored))
        {
            return Task.FromResult<
                IReadOnlyList<TelemetryPacket<T>>>(
                Array.Empty<TelemetryPacket<T>>());
        }

        var store =
            GetTypedStore<T>(
                stored);

        var result =
            store.GetRecent(
                limit,
                cancellationToken);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns a timestamp-ordered, bounded snapshot from the AVL index.
    /// Invalid ranges and limits are rejected even for an empty series.
    /// </summary>
    public Task<IReadOnlyList<TelemetryPacket<T>>> GetRangeAsync<T>(
        Guid sensorProfileId,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toInclusiveUtc,
        int limit,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        ValidateSensorProfileId(sensorProfileId);
        ValidateRange<T>(fromInclusiveUtc, toInclusiveUtc, limit);
        cancellationToken.ThrowIfCancellationRequested();

        var key = CreateStoreKey<T>(sensorProfileId);
        if (!_stores.TryGetValue(key, out var stored))
        {
            return Task.FromResult<IReadOnlyList<TelemetryPacket<T>>>(
                Array.Empty<TelemetryPacket<T>>());
        }

        var result = GetTypedStore<T>(stored).GetRange(
            fromInclusiveUtc, toInclusiveUtc, limit, cancellationToken);
        return Task.FromResult(result);
    }

    private TelemetryStore<T> GetOrCreateStore<T>(
        Guid sensorProfileId)
        where T : struct
    {
        var key =
            CreateStoreKey<T>(
                sensorProfileId);

        var stored =
            _stores.GetOrAdd(
                key,
                _ => new TelemetryStore<T>());

        return GetTypedStore<T>(stored);
    }

    private static TelemetryStore<T> GetTypedStore<T>(
        object stored)
        where T : struct
    {
        // The dictionary key includes typeof(T), so a different generic store
        // appearing under the same key would indicate corrupted repository
        // state rather than invalid client input.
        if (stored is not TelemetryStore<T> typedStore)
        {
            throw new InvalidOperationException(
                "The telemetry repository contains an incompatible " +
                "strongly typed collection.");
        }

        return typedStore;
    }

    private static (Guid SensorId, Type PayloadType)
        CreateStoreKey<T>(
            Guid sensorProfileId)
        where T : struct
    {
        return (
            SensorId: sensorProfileId,
            PayloadType: typeof(T));
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid sensor profile ID is required.",
                nameof(sensorProfileId));
        }
    }

    private static void ValidateRange<T>(
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toInclusiveUtc,
        int limit)
        where T : struct
    {
        if (limit < 1 || limit > AvlTelemetryIndex<T>.MaxRangeLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"Range limit must be between 1 and {AvlTelemetryIndex<T>.MaxRangeLimit}.");
        }

        if (fromInclusiveUtc > toInclusiveUtc)
        {
            throw new ArgumentException(
                "Range start must not be later than range end.",
                nameof(fromInclusiveUtc));
        }
    }

    /// <summary>
    /// Synchronised strongly typed collection used for one sensor/type pair.
    ///
    /// List&lt;T&gt; remains the ingestion-order source of truth, while the
    /// private AVL index references the EXACT same accepted packet objects.
    /// A per-series lock makes both data structures visible as one write.
    /// Lock order is always series lock then AVL lock.
    /// </summary>
    private sealed class TelemetryStore<T>
        where T : struct
    {
        private readonly object _syncRoot =
            new();

        private readonly List<TelemetryPacket<T>> _packets =
            [];

        private readonly AvlTelemetryIndex<T> _timestampIndex =
            new();

        public void Add(
            TelemetryPacket<T> packet,
            CancellationToken cancellationToken)
        {
            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Reserve the append slot before mutating the secondary AVL.
                _packets.EnsureCapacity(checked(_packets.Count + 1));
                _timestampIndex.Insert(packet);
                _packets.Add(packet);
            }
        }

        public void AddRange(
            IEnumerable<TelemetryPacket<T>> packets,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(packets);

            // Materialize and validate before changing the series. Cancellation
            // halfway through an input enumeration must not leave a partial batch.
            cancellationToken.ThrowIfCancellationRequested();
            var acceptedPackets = packets.ToArray();
            foreach (var packet in acceptedPackets)
            {
                ArgumentNullException.ThrowIfNull(packet);
            }

            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // One writer lock covers the complete batch and both views.
                // No reader can observe only a prefix of accepted packets.
                _packets.EnsureCapacity(
                    checked(_packets.Count + acceptedPackets.Length));

                foreach (var packet in acceptedPackets)
                {
                    _timestampIndex.Insert(packet);
                    _packets.Add(packet);
                }
            }
        }

        public IReadOnlyList<TelemetryPacket<T>> GetRecent(
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (_packets.Count == 0)
                {
                    return Array.Empty<TelemetryPacket<T>>();
                }

                var startIndex =
                    Math.Max(
                        0,
                        _packets.Count - limit);

                var count =
                    _packets.Count - startIndex;

                // Return a snapshot so callers cannot observe or mutate the
                // repository's internal collection after the lock is released.
                return _packets
                    .GetRange(
                        startIndex,
                        count)
                    .ToArray();
            }
        }

        public IReadOnlyList<TelemetryPacket<T>> GetRange(
            DateTimeOffset fromInclusiveUtc,
            DateTimeOffset toInclusiveUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The AVL prunes irrelevant timestamp subtrees. This returns
                // an immutable list snapshot, never a mutable tree node.
                return _timestampIndex.QueryRange(
                    fromInclusiveUtc, toInclusiveUtc, limit);
            }
        }
    }
}