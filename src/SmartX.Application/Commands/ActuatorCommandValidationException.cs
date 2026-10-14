namespace SmartX.Application.Commands;

/// <summary>
/// Indicates malformed manual-command input.
///
/// The API exception handler will map this expected failure to HTTP 400
/// when command endpoints are introduced in Commit 3.
/// </summary>
public sealed class ActuatorCommandValidationException : Exception
{
    public ActuatorCommandValidationException(string message)
        : base(message)
    {
    }
}