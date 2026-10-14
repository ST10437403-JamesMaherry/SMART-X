using SmartX.Domain.Commands;

namespace SmartX.Application.Commands;

/// <summary>
/// Describes the actual outcome of an accepted manual command.
///
/// The result is suitable for future API mapping, operational history,
/// and deterministic prediction without re-executing the command.
/// </summary>
public sealed record ActuatorCommandExecutionResult(
    CommandRecord Command,
    RelayState CurrentState)
{
    public bool StateChanged => Command.StateChanged;
}