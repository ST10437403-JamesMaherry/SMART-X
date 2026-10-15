using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Commands;

/// <summary>
/// Coordinates validated manual actuator commands and genuine Stack-backed
/// Undo operations.
///
/// Registered sensor authority remains in ISensorRepository. Simulated
/// commanded relay state is stored independently of sensor operational
/// status and reported telemetry.
///
/// Successful commands and reversals also publish source-linked entries
/// into the separate chronological operational history projection.
/// </summary>
public sealed class ActuatorCommandService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly IActuatorCommandStore _commandStore;
    private readonly TimeProvider _timeProvider;
    private readonly OperationalHistoryRecorder? _historyRecorder;

    public ActuatorCommandService(
        ISensorRepository sensorRepository,
        IActuatorCommandStore commandStore,
        TimeProvider timeProvider,
        OperationalHistoryRecorder? historyRecorder = null)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));

        _commandStore = commandStore ??
            throw new ArgumentNullException(nameof(commandStore));

        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));

        // Optional to preserve existing P2-04 direct-construction tests.
        // Production DI supplies the shared registered recorder.
        _historyRecorder = historyRecorder;
    }

    /// <summary>
    /// Executes a validated manual relay command.
    ///
    /// The authoritative registered device is revalidated inside its
    /// per-device gate. Successful changes are added to the Undo Stack.
    /// Accepted no-ops are audited without becoming undoable.
    ///
    /// Both accepted outcomes appear in chronological operational history.
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
                // Status, category, type and registration authority must
                // be checked at execution time, not against a previous
                // frontend snapshot.
                await ValidateEligibleTargetAsync(
                    targetDeviceId,
                    operationCancellationToken);

                operationCancellationToken.ThrowIfCancellationRequested();

                RelayState previousState =
                    transaction.CurrentState;

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

                // The command store remains the sole authority for
                // commanded state, the real Stack, and permanent audit.
                transaction.Record(command);

                // Publication follows the successful command transaction.
                // Failures in this secondary projection do not create
                // misleading HTTP failures for already-applied commands.
                _historyRecorder?.RecordAcceptedCommand(command);

                return CreateExecutionResult(command, transaction);
            },
            cancellationToken);
    }

    /// <summary>
    /// Restores the recorded previous state of the expected latest
    /// successful state-changing command.
    ///
    /// The expected command ID provides optimistic concurrency control:
    /// stale requests cannot reverse a different Stack entry.
    ///
    /// A successful reversal remains in both the command audit and
    /// operational timeline, but is never pushed back onto the Undo Stack.
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
                // A device must still be registered, Online, and an
                // Actuator/Boolean target when Undo actually executes.
                await ValidateEligibleTargetAsync(
                    targetDeviceId,
                    operationCancellationToken);

                operationCancellationToken.ThrowIfCancellationRequested();

                // Peek without removing the original Stack entry.
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

                // Restore the actual recorded previous state.
                // This must not become a blind Boolean toggle.
                RelayState restoredState =
                    latest.PreviousState;

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

                // The store validates the actual Stack top and restores
                // the previous state before popping the original command.
                //
                // If restoration fails, no reversal history is emitted.
                transaction.Restore(latest, reversal);

                // Record only after the actual reversal succeeds.
                // The original command ID remains linked permanently.
                _historyRecorder?.RecordAcceptedCommand(reversal);

                return CreateExecutionResult(reversal, transaction);
            },
            cancellationToken);
    }

    /// <summary>
    /// Returns the actual state and Undo metadata captured while the
    /// per-device transaction gate is still held.
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
    /// Reads the authoritative registered-device profile while the
    /// command transaction is serialised.
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
    /// Only registered Online Actuator/Boolean devices accept commands.
    ///
    /// Operational status and reported Boolean telemetry are not
    /// substitutes for independently commanded actuator state.
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
    /// Rejects invalid device IDs before allocating command state.
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