using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Coordinates read, update and operational-state operations for registered
/// Smart-X sensors.
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

        IReadOnlyList<SensorProfile> sensors =
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
        ValidateSensorProfileId(
            sensorProfileId);

        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        return SensorContractMapper.ToResponse(
            sensor);
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
        ValidateSensorProfileId(
            sensorProfileId);

        if (request is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor update request is required.");
        }

        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        SensorContractMapper.ApplyUpdate(
            sensor,
            request,
            _timeProvider.GetUtcNow());

        await PersistSensorAsync(
            sensor,
            cancellationToken);

        return SensorContractMapper.ToResponse(
            sensor);
    }

    /// <summary>
    /// Applies an explicit operational-state transition to a registered
    /// sensor.
    ///
    /// Part 1 has no physical ESP32 fleet attached to the application, so this
    /// operation provides a controlled simulation mechanism for demonstrating
    /// disconnections, warnings, faults, maintenance and recovery.
    ///
    /// Domain transition rules remain authoritative. For example, a
    /// decommissioned sensor cannot later be silently reactivated.
    /// </summary>
    public async Task<SensorStatusChangeResult> ChangeStatusAsync(
        Guid sensorProfileId,
        ChangeSensorStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(
            sensorProfileId);

        if (request is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor status request is required.");
        }

        SensorStatus requestedStatus =
            ParseStatus(
                request.Status);

        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        SensorStatus previousStatus =
            sensor.Status;

        /*
         * Repeating the same state is idempotent. Avoid mutating UpdatedAtUtc
         * or generating a false dashboard occurrence when nothing changed.
         */
        if (previousStatus ==
            requestedStatus)
        {
            return new SensorStatusChangeResult(
                previousStatus,
                sensor.Status,
                false,
                SensorContractMapper.ToResponse(
                    sensor));
        }

        cancellationToken.ThrowIfCancellationRequested();

        sensor.ChangeStatus(
            requestedStatus,
            _timeProvider.GetUtcNow());

        await PersistSensorAsync(
            sensor,
            cancellationToken);

        return new SensorStatusChangeResult(
            previousStatus,
            sensor.Status,
            true,
            SensorContractMapper.ToResponse(
                sensor));
    }

    /// <summary>
    /// Marks a previously Offline sensor Online after valid telemetry has been
    /// accepted by the gateway.
    ///
    /// Telemetry is direct evidence that the device is communicating with the
    /// gateway. Warning, Fault, Maintenance and Decommissioned states are not
    /// automatically cleared because receiving a packet alone does not prove
    /// those operational conditions have recovered.
    /// </summary>
    public async Task<SensorStatusChangeResult?> MarkOnlineFromTelemetryAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(
            sensorProfileId);

        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor.Status !=
            SensorStatus.Offline)
        {
            return null;
        }

        SensorStatus previousStatus =
            sensor.Status;

        cancellationToken.ThrowIfCancellationRequested();

        sensor.ChangeStatus(
            SensorStatus.Online,
            _timeProvider.GetUtcNow());

        await PersistSensorAsync(
            sensor,
            cancellationToken);

        return new SensorStatusChangeResult(
            previousStatus,
            sensor.Status,
            true,
            SensorContractMapper.ToResponse(
                sensor));
    }

    /// <summary>
    /// Retrieves one registered Domain sensor.
    /// </summary>
    private async Task<SensorProfile> GetRequiredSensorAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SensorProfile? sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        return sensor;
    }

    /// <summary>
    /// Persists a previously validated sensor mutation.
    /// </summary>
    private async Task PersistSensorAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool updated =
            await _sensorRepository.UpdateAsync(
                sensor,
                cancellationToken);

        if (!updated)
        {
            /*
             * Protect against a future persistence implementation where the
             * profile could disappear between retrieval and persistence.
             */
            throw new SensorNotFoundException(
                sensor.Id);
        }
    }

    /// <summary>
    /// Parses a transport-facing status string into a defined Domain enum
    /// value.
    /// </summary>
    private static SensorStatus ParseStatus(
        string? status)
    {
        if (string.IsNullOrWhiteSpace(
                status))
        {
            throw new SensorRegistrationValidationException(
                "A sensor status is required.");
        }

        string normalised =
            status.Trim();

        if (!Enum.TryParse(
                normalised,
                ignoreCase: true,
                out SensorStatus parsed) ||
            !Enum.IsDefined(
                parsed))
        {
            throw new SensorRegistrationValidationException(
                $"'{normalised}' is not a supported sensor status.");
        }

        return parsed;
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId ==
            Guid.Empty)
        {
            throw new SensorRegistrationValidationException(
                "A valid sensor profile ID is required.");
        }
    }
}