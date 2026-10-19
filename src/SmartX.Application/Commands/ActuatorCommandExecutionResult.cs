using SmartX.Domain.Commands;

namespace SmartX.Application.Commands;

/// <summary>
/// Describes the actual committed outcome of a manual command or Undo.
///
/// The command contains immutable audit metadata, including reversal
/// linkage when the operation was Undo.
///
/// Undo metadata is captured inside the same serialised transaction so
/// callers do not need a separate, potentially stale state read.
/// </summary>
public sealed record ActuatorCommandExecutionResult(
    CommandRecord Command,
    RelayState CurrentState,
    Guid? LatestUndoableCommandId = null,
    int UndoDepth = 0)
{
    /// <summary>
    /// True when the operation actually changed commanded relay state.
    /// </summary>
    public bool StateChanged => Command.StateChanged;

    /// <summary>
    /// True when this operation reversed an earlier manual command.
    /// </summary>
    public bool IsReversal => Command.IsReversal;
}