namespace SmartX.Application.Realtime;

/// <summary>
/// Indicates an invalid operational-history read request.
///
/// Expected input failures are translated into safe HTTP 400
/// ProblemDetails responses by the existing API exception handler.
/// </summary>
public sealed class InvalidRealtimeHistoryQueryException : Exception
{
    public InvalidRealtimeHistoryQueryException(string message)
        : base(message)
    {
    }
}