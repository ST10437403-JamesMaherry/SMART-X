namespace SmartX.Application.Sensors;

/// <summary>
/// Represents invalid sensor registration data detected at the application
/// boundary before a registration can be persisted.
///
/// Domain-specific validation exceptions continue to be raised by the domain
/// model itself. This exception is reserved for transport-to-domain mapping
/// failures such as unsupported identifier kinds or enum names.
/// </summary>
public sealed class SensorRegistrationValidationException : Exception
{
    public SensorRegistrationValidationException(string message)
        : base(message)
    {
    }

    public SensorRegistrationValidationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}