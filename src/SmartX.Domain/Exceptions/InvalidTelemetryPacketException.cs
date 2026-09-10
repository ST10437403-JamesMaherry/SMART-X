namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when incoming telemetry does not satisfy the core Smart-X
/// packet invariants.
/// </summary>
public sealed class InvalidTelemetryPacketException : DomainValidationException
{
    public InvalidTelemetryPacketException(string message)
        : base(message)
    {
    }
}