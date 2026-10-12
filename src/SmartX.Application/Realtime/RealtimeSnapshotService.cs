using SmartX.Application.Sensors;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Projects the existing registered sensors into immutable Part 2 responses.
/// Each read queries the same repository used by registration, metadata,
/// status and telemetry workflows; no transport response is cached as state.
/// </summary>
public sealed class RealtimeSnapshotService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly TimeProvider _timeProvider;

    public RealtimeSnapshotService(
        ISensorRepository sensorRepository,
        TimeProvider timeProvider)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));

        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Returns detached values in the deterministic Part 1 registry order.
    /// The collection wrapper prevents callers from replacing its elements.
    /// </summary>
    public async Task<IReadOnlyList<RealtimeDeviceResponse>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SensorProfile> sensors =
            await _sensorRepository.GetAllAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        RealtimeDeviceResponse[] devices =
            sensors.Select(ToResponse).ToArray();

        return Array.AsReadOnly(devices);
    }

    /// <summary>
    /// Reuses registration's identifier parsing before direct Dictionary
    /// lookup. This path never loads or scans the complete sensor collection.
    /// </summary>
    public async Task<RealtimeDeviceResponse> GetByDeviceIdentifierAsync(
        string? deviceIdentifier,
        string? identifierKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DeviceIdentifier canonical =
            SensorContractMapper.MapDeviceIdentifier(deviceIdentifier, identifierKind);

        SensorProfile? sensor =
            await _sensorRepository.GetByDeviceIdentifierAsync(canonical, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (sensor is null)
        {
            throw new RealtimeDeviceNotFoundException(canonical);
        }

        return ToResponse(sensor);
    }

    /// <summary>
    /// Counts the same detached collection included in the response and uses
    /// the injected clock for a UTC generation timestamp.
    /// </summary>
    public async Task<RealtimeSnapshotResponse> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RealtimeDeviceResponse> devices =
            await GetDevicesAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return new RealtimeSnapshotResponse(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            devices.Count,
            devices);
    }

    private static RealtimeDeviceResponse ToResponse(SensorProfile sensor)
    {
        // The established mapper copies every public profile field, including
        // deployment metadata, without returning mutable Domain entities.
        var profile = SensorContractMapper.ToResponse(sensor);

        return new RealtimeDeviceResponse(
            profile.Id,
            profile.Name,
            profile.DeviceIdentifier,
            profile.IdentifierKind,
            profile.DeploymentLocation,
            profile.Category,
            profile.Status,
            profile.TelemetryDataType,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc,
            null,
            null);
    }
}