using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Commands;

/// <summary>
/// Coordinates validated manual actuator commands and genuine Stack-backed
/// Undo operations.
///
/// The existing sensor repository remains authoritative for registration,
/// category, telemetry type, and operational status.
///
/// Simulated commanded relay state is held separately from sensor health
/// and reported telemetry.
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
    /// Executes a validated manual relay command.
    ///
    /// The registered device is revalidated inside the per-device gate.
    /// Successful state changes become undoable. Accepted no-ops remain
    /// auditable but do not create additional Undo Stack entries.
    /// </summary>
    public Task<ActuatorCommandExecutionResult> ExecuteAsync(
        Guid targetDeviceId,
        RelayCommandType commandType,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetDeviceId);

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
                // Never rely on a device status captured by an earlier
                // frontend snapshot or previous command.
                await ValidateEligibleTargetAsync(
                    targetDeviceId,
                    operationCancellationToken);

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

                // All application validation precedes mutation.
                // The store performs its own final state validation.
                transaction.Record(command);

                return CreateExecutionResult(command, transaction);
            },
            cancellationToken);
    }

    /// <summary>
    /// Undoes the expected latest successful state-changing command.
    ///
    /// This is a genuine restoration of the original PreviousState.
    /// It does not blindly toggle the current relay state.
    ///
    /// The expected command ID provides optimistic concurrency protection:
    /// stale requests cannot reverse a different command that has since
    /// become the Stack top.
    ///
    /// A reversal is audited with a link to the original command, but is
    /// never pushed back onto the Undo Stack.
    /// </summary>
    public Task<ActuatorCommandExecutionResult> UndoAsync(
        Guid targetDeviceId,
        Guid expectedLatestCommandId,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetDeviceId);

        if (expectedLatestCommandId == Guid.Empty)
        {
            throw new ActuatorCommandValidationException(
                "A valid expected latest command ID is required for Undo.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return _commandStore.ExecuteSerializedAsync(
            targetDeviceId,
            async (transaction, operationCancellationToken) =>
            {
                // Eligibility must still be valid when Undo executes.
                // A device that has gone Offline, Faulted, or entered
                // Maintenance must not receive a reversal command.
                await ValidateEligibleTargetAsync(
                    targetDeviceId,
                    operationCancellationToken);

                operationCancellationToken.ThrowIfCancellationRequested();

                // Peek, do not pop. The underlying store owns the Stack.
                CommandRecord? latest =
                    transaction.LatestUndoableCommand;

                if (latest is null)
                {
                    throw new ActuatorCommandConflictException(
                        "This device has no state-changing command to undo.");
                }

                if (latest.Id != expectedLatestCommandId)
                {
                    throw new ActuatorCommandConflictException(
                        "The requested Undo is stale. The latest " +
                        "undoable command has changed.");
                }

                // Defensive checks prevent reversals, no-ops, or another
                // device's commands from becoming Undo candidates.
                if (latest.TargetDeviceId != targetDeviceId ||
                    latest.IsReversal ||
                    !latest.StateChanged)
                {
                    throw new ActuatorCommandConflictException(
                        "The latest command is not a valid Undo target.");
                }

                if (transaction.CurrentState != latest.ResultingState)
                {
                    throw new ActuatorCommandConflictException(
                        "The actuator state no longer matches the " +
                        "latest undoable command.");
                }

                // Restore the state recorded before the original command.
                // This is deliberately not a generic toggle operation.
                RelayState restoredState = latest.PreviousState;

                RelayCommandType reversalType =
                    restoredState == RelayState.On
                        ? RelayCommandType.RelayOn
                        : RelayCommandType.RelayOff;

                CommandRecord reversal = new(
                    Guid.NewGuid(),
                    targetDeviceId,
                    reversalType,
                    _timeProvider.GetUtcNow(),
                    transaction.CurrentState,
                    restoredState,
                    latest.Id);

                operationCancellationToken.ThrowIfCancellationRequested();

                // The store revalidates the expected Stack top and performs
                // audit insertion, state restoration, and Stack removal
                // while holding the same per-device gate.
                //
                // Restore must throw without mutation if reversal validation
                // or audit recording fails.
                transaction.Restore(latest, reversal);

                return CreateExecutionResult(reversal, transaction);
            },
            cancellationToken);
    }

    /// <summary>
    /// Returns the committed state and Undo metadata observed inside the
    /// current serialised transaction.
    /// </summary>
    private static ActuatorCommandExecutionResult CreateExecutionResult(
        CommandRecord command,
        IActuatorCommandTransaction transaction)
    {
        return new ActuatorCommandExecutionResult(
            command,
            transaction.CurrentState,
            transaction.LatestUndoableCommand?.Id,
            transaction.UndoDepth);
    }

    /// <summary>
    /// Validates that the target is still registered and eligible.
    ///
    /// Both Execute and Undo call this method from inside their per-device
    /// serialisation gate.
    /// </summary>
    private async Task ValidateEligibleTargetAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SensorProfile? sensor =
            await _sensorRepository.GetByIdAsync(
                targetDeviceId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(targetDeviceId);
        }

        ValidateEligibility(sensor);
    }

    /// <summary>
    /// Only explicitly Online Actuator/Boolean devices are eligible.
    ///
    /// Reported Boolean telemetry does not determine commanded relay state.
    /// All non-Online operational statuses are intentionally rejected.
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

    /// <summary>
    /// Rejects malformed target identifiers before allocating transaction
    /// state or accessing the registered-device repository.
    /// </summary>
    private static void ValidateTargetId(Guid targetDeviceId)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new ActuatorCommandValidationException(
                "A valid target device ID is required.");
        }
    }
}