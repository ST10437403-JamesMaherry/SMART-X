using SmartX.Contracts.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Coordinates read and update operations for registered Smart-X sensors.
///
/// Registration remains in SensorRegistrationService because creating a new
/// physical-device identity and managing an existing profile are separate
/// application responsibilities.
/// </summary>
public sealed class SensorManagementService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly TimeProvider _timeProvider;

    public SensorManagementService(
        ISensorRepository sensorRepository,
        TimeProvider timeProvider)
    {
        _sensorRepository =
            sensorRepository ??
            throw new ArgumentNullException(
                nameof(sensorRepository));

        _timeProvider =
            timeProvider ??
            throw new ArgumentNullException(
                nameof(timeProvider));
    }

    /// <summary>
    /// Returns all registered sensor profiles as transport-safe responses.
    /// </summary>
    public async Task<IReadOnlyList<SensorResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sensors =
            await _sensorRepository.GetAllAsync(
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return sensors
            .Select(SensorContractMapper.ToResponse)
            .ToArray();
    }

    /// <summary>
    /// Returns one registered sensor or raises a meaningful application
    /// exception when the supplied ID does not exist.
    /// </summary>
    public async Task<SensorResponse> GetByIdAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        return SensorContractMapper.ToResponse(sensor);
    }

    /// <summary>
    /// Updates mutable sensor metadata while preserving sensor and physical
    /// device identity.
    /// </summary>
    public async Task<SensorResponse> UpdateAsync(
        Guid sensorProfileId,
        UpdateSensorRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);

        if (request is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor update request is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        SensorContractMapper.ApplyUpdate(
            sensor,
            request,
            _timeProvider.GetUtcNow());

        cancellationToken.ThrowIfCancellationRequested();

        var updated =
            await _sensorRepository.UpdateAsync(
                sensor,
                cancellationToken);

        if (!updated)
        {
            // This protects against a future persistence implementation where
            // the record could disappear between retrieval and update.
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        return SensorContractMapper.ToResponse(sensor);
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new SensorRegistrationValidationException(
                "A valid sensor profile ID is required.");
        }
    }
}