using SmartX.Contracts.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Coordinates registration of validated Smart-X sensor profiles.
///
/// Responsibilities are intentionally limited to application orchestration:
/// mapping input, creating the domain entity, requesting atomic persistence,
/// handling duplicate identifiers and mapping the successful result back to
/// an API contract.
/// </summary>
public sealed class SensorRegistrationService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly TimeProvider _timeProvider;

    public SensorRegistrationService(
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
    /// Registers a new Smart-X sensor asynchronously.
    ///
    /// Cancellation is checked before domain work and propagated to the
    /// repository so callers can abandon registrations that are no longer
    /// required.
    /// </summary>
    public async Task<SensorResponse> RegisterAsync(
        RegisterSensorRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor registration request is required.");
        }

        var sensor =
            SensorContractMapper.ToDomain(
                request,
                _timeProvider.GetUtcNow());

        // Cancellation may have been requested while validation and mapping
        // were occurring. Avoid beginning persistence when the caller has
        // already abandoned the operation.
        cancellationToken.ThrowIfCancellationRequested();

        var added =
            await _sensorRepository.TryAddAsync(
                sensor,
                cancellationToken);

        if (!added)
        {
            throw new DuplicateSensorIdentifierException(
                sensor.DeviceIdentifier);
        }

        return SensorContractMapper.ToResponse(sensor);
    }
}