namespace SmartX.Application.Attachments;

/// <summary>
/// Represents an expected validation failure while processing a sensor
/// attachment.
///
/// This exception allows the API layer to distinguish invalid client input
/// from unexpected infrastructure or cryptographic failures.
/// </summary>
public sealed class InvalidSensorAttachmentException : Exception
{
    public InvalidSensorAttachmentException(string message)
        : base(message)
    {
    }

    public InvalidSensorAttachmentException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}