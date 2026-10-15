namespace SmartX.Application.Realtime;

/// <summary>
/// Represents an invalid device-discovery query. The existing API exception
/// handler translates this expected client error into HTTP 400 ProblemDetails.
/// </summary>
public sealed class InvalidRealtimeDeviceSearchQueryException : Exception
{
    public InvalidRealtimeDeviceSearchQueryException(string message)
        : base(message)
    {
    }
}