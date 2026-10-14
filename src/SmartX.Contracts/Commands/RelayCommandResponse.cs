namespace SmartX.Contracts.Commands;

/// <summary>
/// Immutable transport representation of one accepted manual command
/// or a successful reversal.
///
/// Reversal metadata points back to the original command record,
/// allowing operators to verify what an Undo actually restored.
/// </summary>
public sealed record CommandAuditResponse(
    Guid CommandId,
    Guid TargetDeviceId,
    string CommandType,
    DateTimeOffset ExecutedAtUtc,
    string PreviousState,
    string ResultingState,
    bool StateChanged,
    bool IsReversal,
    Guid? ReversesCommandId);

/// <summary>
/// Authoritative simulated commanded state for one actuator.
///
/// This is intentionally separate from the registered sensor's
/// operational status and latest reported Boolean telemetry.
///
/// UndoDepth and LatestUndoableCommandId describe the real
/// per-device Stack, not the size of the permanent audit trail.
/// </summary>
public sealed record ActuatorCommandStateResponse(
    Guid TargetDeviceId,
    string CommandedState,
    Guid? LatestUndoableCommandId,
    int UndoDepth);

/// <summary>
/// Response returned after a successful manual command or Undo.
///
/// Both the actual committed command record and the resulting
/// actuator state are captured by the command service inside
/// the same serialised transaction.
/// </summary>
public sealed record RelayCommandResponse(
    CommandAuditResponse Command,
    ActuatorCommandStateResponse Actuator);

/// <summary>
/// Read-only command history for a registered actuator.
///
/// TotalAuditCount reflects every accepted command and reversal.
/// RecentAudit contains up to 100 records in audit insertion order,
/// oldest to newest within the retained response window.
///
/// The permanent process-local audit is not truncated by this DTO.
/// </summary>
public sealed record ActuatorCommandHistoryResponse(
    ActuatorCommandStateResponse Actuator,
    int TotalAuditCount,
    IReadOnlyList<CommandAuditResponse> RecentAudit);