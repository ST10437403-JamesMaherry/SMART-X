namespace SmartX.Application.Commands;

/// <summary>
/// Indicates that an otherwise valid command cannot be performed because
/// the target is ineligible or its commanded state is no longer expected.
///
/// Commit 3 will map this condition to HTTP 409 ProblemDetails.
/// </summary>
public sealed class ActuatorCommandConflictException : Exception
{
    public ActuatorCommandConflictException(string message)
        : base(message)
    {
    }
}