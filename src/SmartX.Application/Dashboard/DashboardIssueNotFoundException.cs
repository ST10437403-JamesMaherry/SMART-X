namespace SmartX.Application.Dashboard;

/// <summary>
/// Raised when an operation targets a dashboard issue that no longer exists.
/// </summary>
public sealed class DashboardIssueNotFoundException : Exception
{
    public DashboardIssueNotFoundException(
        Guid issueId)
        : base(
            $"Dashboard issue '{issueId}' was not found.")
    {
        IssueId =
            issueId;
    }

    public Guid IssueId { get; }
}