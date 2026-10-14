using SmartX.Contracts.Commands;
using SmartX.Domain.Commands;

namespace SmartX.Application.Commands;

/// <summary>
/// Maps authoritative command Domain/Application results into detached
/// HTTP transport contracts.
///
/// Mapping is deliberately independent of controllers so future
/// operational history and recommendation features can reuse the
/// same command metadata without executing another command.
/// </summary>
public static class ActuatorCommandContractMapper
{
    /// <summary>
    /// Maximum number of audit records returned by an ordinary HTTP
    /// history read. The underlying audit remains process-local
    /// and is not truncated by this presentation limit.
    /// </summary>
    public const int MaxRecentAuditRecords = 100;

    /// <summary>
    /// Maps the result of a successfully committed command or Undo.
    /// The application service already captured the state and Stack
    /// metadata inside the original device transaction.
    /// </summary>
    public static RelayCommandResponse ToResponse(
        ActuatorCommandExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RelayCommandResponse(
            ToAuditResponse(result.Command),
            new ActuatorCommandStateResponse(
                result.Command.TargetDeviceId,
                result.CurrentState.ToString(),
                result.LatestUndoableCommandId,
                result.UndoDepth));
    }

    /// <summary>
    /// Projects a detached store snapshot into lightweight
    /// commanded-state metadata for the real-time device list.
    /// </summary>
    public static ActuatorCommandStateResponse ToStateResponse(
        ActuatorStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ActuatorCommandStateResponse(
            snapshot.TargetDeviceId,
            snapshot.CurrentState.ToString(),
            snapshot.LatestUndoableCommandId,
            snapshot.UndoDepth);
    }

    /// <summary>
    /// Maps a detached command audit snapshot into a bounded HTTP
    /// response while preserving its actual insertion order.
    ///
    /// No ordering is reconstructed from timestamps because equal
    /// timestamps can legitimately occur during concurrent requests.
    /// </summary>
    public static ActuatorCommandHistoryResponse ToHistoryResponse(
        ActuatorStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        CommandAuditResponse[] recent =
            snapshot.AuditTrail
                .TakeLast(MaxRecentAuditRecords)
                .Select(ToAuditResponse)
                .ToArray();

        return new ActuatorCommandHistoryResponse(
            ToStateResponse(snapshot),
            snapshot.AuditTrail.Count,
            Array.AsReadOnly(recent));
    }

    /// <summary>
    /// Preserves all original command identifiers, timestamps,
    /// previous/resulting states, and reversal relationships.
    /// </summary>
    public static CommandAuditResponse ToAuditResponse(
        CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new CommandAuditResponse(
            command.Id,
            command.TargetDeviceId,
            command.Type.ToString(),
            command.ExecutedAtUtc,
            command.PreviousState.ToString(),
            command.ResultingState.ToString(),
            command.StateChanged,
            command.IsReversal,
            command.ReversesCommandId);
    }
}