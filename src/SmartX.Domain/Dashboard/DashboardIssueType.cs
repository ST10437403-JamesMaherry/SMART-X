namespace SmartX.Domain.Dashboard;

/// <summary>
/// Identifies the operational condition represented by a Smart-X dashboard
/// issue.
///
/// These categories map directly to the Part 1 Adaptive Anomaly Spotlight
/// requirements and allow later application services to resolve issues when
/// the underlying condition clears.
/// </summary>
public enum DashboardIssueType
{
    TelemetrySpike = 1,
    SensorDisconnected = 2,
    RepeatedFailure = 3,
    ConfigurationIssue = 4,
    SensorStateChange = 5
}