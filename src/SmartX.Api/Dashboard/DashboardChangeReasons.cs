namespace SmartX.Api.Dashboard;

/// <summary>
/// Stable reason values attached to dashboard invalidation events.
///
/// Constants avoid fragile duplicated string literals across controllers,
/// coordinators and the future React SignalR client.
/// </summary>
public static class DashboardChangeReasons
{
    public const string SensorRegistered =
        "sensor-registered";

    public const string TelemetryChanged =
        "telemetry-changed";

    public const string SensorStatusChanged =
        "sensor-status-changed";

    public const string ConfigurationChanged =
        "configuration-changed";

    public const string IssueAcknowledged =
        "issue-acknowledged";
}