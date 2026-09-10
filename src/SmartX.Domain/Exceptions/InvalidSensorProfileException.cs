namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when a sensor profile violates a Smart-X domain rule.
/// </summary>
public sealed class InvalidSensorProfileException : DomainValidationException
{
    public InvalidSensorProfileException(string message)
        : base(message)
    {
    }
}