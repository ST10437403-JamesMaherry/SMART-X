namespace SmartX.Domain.Commands;

/// <summary>
/// An immutable record describing an accepted manual command or reversal.
///
/// Previous and resulting states are retained so Undo can restore the
/// actual prior commanded state rather than simply toggling a boolean.
/// </summary>
public sealed record CommandRecord
{
    public CommandRecord(
        Guid id,
        Guid targetDeviceId,
        RelayCommandType type,
        DateTimeOffset executedAtUtc,
        RelayState previousState,
        RelayState resultingState,
        Guid? reversesCommandId = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A command ID is required.",
                nameof(id));
        }

        if (targetDeviceId == Guid.Empty)
        {
            throw new ArgumentException(
                "A target device ID is required.",
                nameof(targetDeviceId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "The relay command type is unsupported.");
        }

        if (!Enum.IsDefined(previousState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousState));
        }

        if (!Enum.IsDefined(resultingState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultingState));
        }

        if (executedAtUtc == default)
        {
            throw new ArgumentException(
                "A command execution timestamp is required.",
                nameof(executedAtUtc));
        }

        // The command vocabulary determines the requested resulting state.
        // PreviousState may equal ResultingState for an accepted no-op.
        RelayState expectedResult = type == RelayCommandType.RelayOn
            ? RelayState.On
            : RelayState.Off;

        if (resultingState != expectedResult)
        {
            throw new ArgumentException(
                "The resulting relay state does not match the command type.",
                nameof(resultingState));
        }

        if (reversesCommandId.HasValue &&
            (reversesCommandId.Value == Guid.Empty ||
             reversesCommandId.Value == id))
        {
            throw new ArgumentException(
                "A reversal must reference a different valid command ID.",
                nameof(reversesCommandId));
        }

        Id = id;
        TargetDeviceId = targetDeviceId;
        Type = type;
        ExecutedAtUtc = executedAtUtc.ToUniversalTime();
        PreviousState = previousState;
        ResultingState = resultingState;
        ReversesCommandId = reversesCommandId;
    }

    public Guid Id { get; }

    public Guid TargetDeviceId { get; }

    public RelayCommandType Type { get; }

    public DateTimeOffset ExecutedAtUtc { get; }

    public RelayState PreviousState { get; }

    public RelayState ResultingState { get; }

    /// <summary>
    /// References the original command when this record represents Undo.
    /// Null for ordinary manual commands.
    /// </summary>
    public Guid? ReversesCommandId { get; }

    public bool StateChanged => PreviousState != ResultingState;

    public bool IsReversal => ReversesCommandId.HasValue;
}