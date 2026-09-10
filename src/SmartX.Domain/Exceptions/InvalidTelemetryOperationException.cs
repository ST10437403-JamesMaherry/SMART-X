namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when telemetry packets cannot participate in a requested
/// domain-level operation.
///
/// Examples include comparing readings from different physical devices or
/// attempting to calculate a delta in reverse chronological order.
/// </summary>
public sealed class InvalidTelemetryOperationException
    : DomainValidationException
{
    public InvalidTelemetryOperationException(string message)
        : base(message)
    {
    }
}