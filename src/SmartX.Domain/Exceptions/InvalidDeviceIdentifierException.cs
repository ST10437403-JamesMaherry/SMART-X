namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when a MAC address or custom device identifier does not satisfy
/// the Smart-X device identification rules.
/// </summary>
public sealed class InvalidDeviceIdentifierException : DomainValidationException
{
    public InvalidDeviceIdentifierException(string message)
        : base(message)
    {
    }
}
