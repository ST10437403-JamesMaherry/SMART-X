namespace SmartX.Contracts.Commands;

/// <summary>
/// Requests restoration of the previous commanded actuator state.
///
/// ExpectedLatestCommandId is an optimistic concurrency condition.
/// The operation is rejected when that command is no longer at the
/// top of the device's actual Undo Stack.
/// </summary>
public sealed record UndoRelayCommandRequest(
    Guid? ExpectedLatestCommandId);