namespace SmartX.Domain.Dashboard;

/// <summary>
/// Describes the operational importance of an Adaptive Anomaly Spotlight
/// issue.
///
/// Explicit severity values provide a stable ordering from informational
/// conditions through warnings to situations requiring immediate operator
/// attention.
/// </summary>
public enum DashboardIssueSeverity
{
    Information = 1,
    Warning = 2,
    Critical = 3
}