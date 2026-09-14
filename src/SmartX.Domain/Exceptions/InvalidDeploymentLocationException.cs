namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when a sensor deployment location is incomplete or invalid.
/// </summary>
public sealed class InvalidDeploymentLocationException : DomainValidationException
{
    public InvalidDeploymentLocationException(string message)
        : base(message)
    {
    }
}