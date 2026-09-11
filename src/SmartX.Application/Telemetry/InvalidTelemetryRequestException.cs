namespace SmartX.Application.Telemetry;

/// <summary>
/// Represents malformed or incompatible telemetry received at the
/// Application boundary.
///
/// Domain telemetry exceptions continue to represent Domain invariant
/// failures, while this exception describes transport-to-domain problems
/// such as missing values or a value incompatible with the sensor's
/// configured telemetry type.
/// </summary>
public sealed class InvalidTelemetryRequestException : Exception
{
    public InvalidTelemetryRequestException(string message)
        : base(message)
    {
    }

    public InvalidTelemetryRequestException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}