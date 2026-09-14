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
    /// Stores one strongly typed telemetry packet.
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

        store.Add(packet);

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

    /// <summary>
    /// Synchronised strongly typed collection used for one sensor/type pair.
    ///
    /// List&lt;T&gt; gives efficient append and sequential-read behaviour while
    /// an explicit lock protects compound operations from concurrent access.
    /// </summary>
    private sealed class TelemetryStore<T>
        where T : struct
    {
        private readonly object _syncRoot =
            new();

        private readonly List<TelemetryPacket<T>> _packets =
            [];

        public void Add(
            TelemetryPacket<T> packet)
        {
            lock (_syncRoot)
            {
                _packets.Add(packet);
            }
        }

        public void AddRange(
            IEnumerable<TelemetryPacket<T>> packets,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(packets);

            lock (_syncRoot)
            {
                foreach (var packet in packets)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ArgumentNullException.ThrowIfNull(packet);

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
    }
}