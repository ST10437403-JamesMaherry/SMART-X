namespace SmartX.Domain.Commands;

/// <summary>
/// A detached view of one device's simulated commanded relay state.
///
/// The audit trail includes accepted manual commands, including no-ops.
/// Commit 2 will add separate Stack-backed Undo metadata.
/// </summary>
public sealed record ActuatorStateSnapshot(
    Guid TargetDeviceId,
    RelayState CurrentState,
    IReadOnlyList<CommandRecord> AuditTrail);