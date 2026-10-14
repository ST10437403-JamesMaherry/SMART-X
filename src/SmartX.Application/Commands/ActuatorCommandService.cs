using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Commands;

/// <summary>
/// Coordinates validated manual actuator commands.
///
/// The existing sensor repository remains authoritative for registration,
/// category, telemetry type, and operational status. Commanded relay state
/// belongs to the separate command store and never modifies sensor telemetry.
/// </summary>
public sealed class ActuatorCommandService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly IActuatorCommandStore _commandStore;
    private readonly TimeProvider _timeProvider;

    public ActuatorCommandService(
        ISensorRepository sensorRepository,
        IActuatorCommandStore commandStore,
        TimeProvider timeProvider)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));

        _commandStore = commandStore ??
            throw new ArgumentNullException(nameof(commandStore));

        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Executes a manual relay command against a registered eligible device.
    ///
    /// Eligibility is checked within the per-device serialisation boundary,
    /// not against an old UI snapshot captured before execution.
    ///
    /// An accepted no-op receives an audit record but does not create a
    /// state change. Failed commands cannot mutate commanded relay state.
    /// </summary>
    public Task<ActuatorCommandExecutionResult> ExecuteAsync(
        Guid targetDeviceId,
        RelayCommandType commandType,
        CancellationToken cancellationToken = default)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new ActuatorCommandValidationException(
                "A valid target device ID is required.");
        }

        if (!Enum.IsDefined(commandType))
        {
            throw new ActuatorCommandValidationException(
                $"'{commandType}' is not a supported relay command.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return _commandStore.ExecuteSerializedAsync(
            targetDeviceId,
            async (transaction, operationCancellationToken) =>
            {
                operationCancellationToken.ThrowIfCancellationRequested();

                // Resolve the authoritative profile inside the device gate.
                SensorProfile? sensor =
                    await _sensorRepository.GetByIdAsync(
                        targetDeviceId,
                        operationCancellationToken);

                if (sensor is null)
                {
                    throw new SensorNotFoundException(targetDeviceId);
                }

                ValidateEligibility(sensor);

                operationCancellationToken.ThrowIfCancellationRequested();

                RelayState previousState = transaction.CurrentState;

                RelayState resultingState =
                    commandType == RelayCommandType.RelayOn
                        ? RelayState.On
                        : RelayState.Off;

                CommandRecord command = new(
                    Guid.NewGuid(),
                    targetDeviceId,
                    commandType,
                    _timeProvider.GetUtcNow(),
                    previousState,
                    resultingState);

                // Recording is the final step. All request, authority,
                // eligibility, and cancellation checks precede mutation.
                transaction.Record(command);

                return new ActuatorCommandExecutionResult(
                    command,
                    transaction.CurrentState);
            },
            cancellationToken);
    }

    /// <summary>
    /// A device must be both an actuator and a Boolean telemetry source.
    /// Being registered or having a recent reading is not sufficient.
    ///
    /// Warning, Fault, Maintenance, Offline, and Decommissioned are all
    /// intentionally rejected; only an explicitly Online device is eligible.
    /// </summary>
    private static void ValidateEligibility(SensorProfile sensor)
    {
        if (sensor.Category != SensorCategory.Actuator ||
            sensor.DataType != TelemetryDataType.Boolean)
        {
            throw new ActuatorCommandConflictException(
                "Manual relay commands require a registered " +
                "Actuator device with Boolean telemetry.");
        }

        if (sensor.Status != SensorStatus.Online)
        {
            throw new ActuatorCommandConflictException(
                $"Device '{sensor.Id}' cannot accept commands while " +
                $"its operational status is '{sensor.Status}'.");
        }
    }
}