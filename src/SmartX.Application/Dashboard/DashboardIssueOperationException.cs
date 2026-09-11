namespace SmartX.Application.Dashboard;

/// <summary>
/// Represents a valid dashboard resource that cannot perform the requested
/// state transition.
///
/// This is separated from a missing-resource failure so the HTTP layer can
/// return a meaningful conflict response rather than a generic server error.
/// </summary>
public sealed class DashboardIssueOperationException : Exception
{
    public DashboardIssueOperationException(
        string message)
        : base(message)
    {
    }
}