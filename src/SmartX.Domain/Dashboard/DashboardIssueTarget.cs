namespace SmartX.Domain.Dashboard;

/// <summary>
/// Identifies the most useful Smart-X workspace section for investigating a
/// dashboard issue.
///
/// The React client will later use this value to move directly from the
/// Adaptive Anomaly Spotlight to the relevant evidence instead of requiring
/// an operator to search manually.
/// </summary>
public enum DashboardIssueTarget
{
    Sensor = 1,
    Telemetry = 2,
    Attachments = 3
}