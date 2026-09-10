namespace SmartX.Domain.Exceptions;

/// <summary>
/// Base exception for violations of Smart-X domain rules.
///
/// Using a domain-specific base exception allows API and application layers
/// to distinguish expected validation failures from unexpected system errors.
/// </summary>
public abstract class DomainValidationException : Exception
{
    protected DomainValidationException(string message)
        : base(message)
    {
    }

    protected DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}