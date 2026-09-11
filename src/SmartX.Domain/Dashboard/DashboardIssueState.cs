namespace SmartX.Domain.Dashboard;

/// <summary>
/// Represents the operator lifecycle of a dashboard issue.
///
/// Active issues require attention, acknowledged issues have been seen by an
/// operator but remain unresolved, and resolved issues represent conditions
/// that are no longer active.
/// </summary>
public enum DashboardIssueState
{
    Active = 1,
    Acknowledged = 2,
    Resolved = 3
}