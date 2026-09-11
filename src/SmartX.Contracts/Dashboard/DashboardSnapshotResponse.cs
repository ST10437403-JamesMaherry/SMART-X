namespace SmartX.Contracts.Dashboard;

/// <summary>
/// Represents the current server-authoritative Adaptive Anomaly Spotlight
/// state returned to the React client.
/// </summary>
public sealed record DashboardSnapshotResponse(
    DateTimeOffset GeneratedAtUtc,
    int ActiveIssueCount,
    int UnacknowledgedIssueCount,
    IReadOnlyList<DashboardIssueResponse> Issues);