namespace SmartX.Domain.Commands;

/// <summary>
/// A detached view of one device's simulated commanded relay state.
///
/// Commanded state is independent of operational sensor status and reported
/// telemetry. An actuator with no command history begins explicitly Off.
///
/// The audit trail retains accepted manual commands and successful
/// reversals, while UndoDepth counts only changes still available to Undo.
/// </summary>
public sealed record ActuatorStateSnapshot(
    Guid TargetDeviceId,
    RelayState CurrentState,
    IReadOnlyList<CommandRecord> AuditTrail,
    Guid? LatestUndoableCommandId = null,
    int UndoDepth = 0);