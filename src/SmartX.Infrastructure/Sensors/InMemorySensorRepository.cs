using System.Collections.Concurrent;

using SmartX.Application.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Infrastructure.Sensors;

/// <summary>
/// Provides thread-safe, process-local persistence for registered Smart-X
/// sensors.
///
/// Part 1 does not require an external database, so an in-memory repository
/// keeps the backend lightweight while still preserving the repository
/// abstraction required by the Application layer.
///
/// A second concurrent dictionary indexes normalised device identifiers.
/// This allows duplicate physical devices to be rejected atomically without
/// repeatedly scanning every registered sensor.
/// </summary>
public sealed class InMemorySensorRepository : ISensorRepository
{
    private readonly ConcurrentDictionary<Guid, SensorProfile> _sensors =
        new();

    private readonly ConcurrentDictionary<string, Guid> _identifierIndex =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Attempts to register a sensor atomically.
    ///
    /// The identifier index is written first so concurrent requests using
    /// the same normalised identifier cannot both succeed.
    /// </summary>
    public Task<bool> TryAddAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        cancellationToken.ThrowIfCancellationRequested();

        var identifierKey =
            CreateIdentifierKey(
                sensor.DeviceIdentifier);

        // Only one concurrent registration may claim a physical identifier.
        if (!_identifierIndex.TryAdd(
                identifierKey,
                sensor.Id))
        {
            return Task.FromResult(false);
        }

        // Guid collisions are exceptionally unlikely, but the repository
        // still handles the condition safely rather than assuming success.
        if (!_sensors.TryAdd(
                sensor.Id,
                sensor))
        {
            _identifierIndex.TryRemove(
                identifierKey,
                out _);

            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Returns a stable snapshot of all registered sensors.
    ///
    /// Results are ordered by creation time and then ID so API responses are
    /// deterministic even though ConcurrentDictionary itself is unordered.
    /// </summary>
    public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SensorProfile> sensors =
            _sensors
                .Values
                .OrderBy(sensor => sensor.CreatedAtUtc)
                .ThenBy(sensor => sensor.Id)
                .ToArray();

        return Task.FromResult(sensors);
    }

    /// <summary>
    /// Retrieves one sensor by its internal Smart-X ID.
    /// </summary>
    public Task<SensorProfile?> GetByIdAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _sensors.TryGetValue(
            sensorProfileId,
            out var sensor);

        return Task.FromResult(sensor);
    }

    /// <summary>
    /// Persists changes to an existing sensor profile.
    ///
    /// Device identity is immutable in SensorProfile, so the repository only
    /// replaces an entry when the stored and supplied physical identifiers
    /// still match.
    /// </summary>
    public Task<bool> UpdateAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_sensors.TryGetValue(
                sensor.Id,
                out var existing))
        {
            return Task.FromResult(false);
        }

        if (existing.DeviceIdentifier !=
            sensor.DeviceIdentifier)
        {
            return Task.FromResult(false);
        }

        _sensors[sensor.Id] =
            sensor;

        return Task.FromResult(true);
    }

    private static string CreateIdentifierKey(
        DeviceIdentifier deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentifier);

        // Including the identifier kind prevents conceptually different
        // identifier types from sharing one index namespace.
        return
            $"{deviceIdentifier.Kind}:{deviceIdentifier.Value}";
    }
}