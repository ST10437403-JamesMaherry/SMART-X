using SmartX.Application.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Infrastructure.Sensors;

/// <summary>
/// Provides process-local sensor persistence using the standard Dictionary
/// collection required by Part 2. Both indexes reference the same registered
/// profile; no separate live-device registry owns duplicate sensor state.
/// </summary>
public sealed class InMemorySensorRepository : ISensorRepository
{
    // Protect dictionary membership and paired-index operations with one
    // short lock. No asynchronous work or I/O runs inside this boundary.
    private readonly object _syncRoot = new();

    private readonly Dictionary<Guid, SensorProfile> _sensors = new();

    private readonly Dictionary<string, SensorProfile> _identifierIndex =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes both identities atomically, rejecting duplicate GUIDs or
    /// canonical physical identifiers without leaving a partial registration.
    /// </summary>
    public Task<bool> TryAddAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        cancellationToken.ThrowIfCancellationRequested();

        string identifierKey =
            CreateIdentifierKey(sensor.DeviceIdentifier);

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_sensors.ContainsKey(sensor.Id) ||
                _identifierIndex.ContainsKey(identifierKey))
            {
                return Task.FromResult(false);
            }

            _sensors.Add(sensor.Id, sensor);
            _identifierIndex.Add(identifierKey, sensor);

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Copies registry membership under the lock, then preserves Part 1's
    /// creation-time/GUID ordering outside the critical section.
    /// Domain profiles retain the existing application mutation workflow;
    /// this array is a membership snapshot rather than a deep entity copy.
    /// </summary>
    public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SensorProfile[] registered;

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            registered = _sensors.Values.ToArray();
        }

        IReadOnlyList<SensorProfile> ordered =
            registered
                .OrderBy(sensor => sensor.CreatedAtUtc)
                .ThenBy(sensor => sensor.Id)
                .ToArray();

        return Task.FromResult(ordered);
    }

    /// <summary>
    /// Retrieves the registered profile by its internal Smart-X GUID.
    /// </summary>
    public Task<SensorProfile?> GetByIdAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _sensors.TryGetValue(sensorProfileId, out var sensor);

            return Task.FromResult(sensor);
        }
    }

    /// <summary>
    /// Performs expected O(1) exact lookup using a validated, canonical
    /// physical identifier. It does not scan profiles or perform text search.
    /// </summary>
    public Task<SensorProfile?> GetByDeviceIdentifierAsync(
        DeviceIdentifier deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentifier);

        cancellationToken.ThrowIfCancellationRequested();

        string identifierKey =
            CreateIdentifierKey(deviceIdentifier);

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _identifierIndex.TryGetValue(identifierKey, out var sensor);

            return Task.FromResult(sensor);
        }
    }

    /// <summary>
    /// Retains the registered identity and updates both indexes together.
    /// This also supports a future detached profile with the same identities.
    /// </summary>
    public Task<bool> UpdateAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        cancellationToken.ThrowIfCancellationRequested();

        string identifierKey =
            CreateIdentifierKey(sensor.DeviceIdentifier);

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sensors.TryGetValue(sensor.Id, out var existing) ||
                existing.DeviceIdentifier != sensor.DeviceIdentifier)
            {
                return Task.FromResult(false);
            }

            _sensors[sensor.Id] = sensor;
            _identifierIndex[identifierKey] = sensor;

            return Task.FromResult(true);
        }
    }

    private static string CreateIdentifierKey(DeviceIdentifier deviceIdentifier)
    {
        // Domain factories normalise MAC formats. Ordinal comparison keeps
        // custom-ID case significant, and the kind qualifies each namespace.
        return $"{deviceIdentifier.Kind}:{deviceIdentifier.Value}";
    }
}