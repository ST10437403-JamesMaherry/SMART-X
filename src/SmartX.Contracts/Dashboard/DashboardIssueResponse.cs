namespace SmartX.Contracts.Dashboard;

/// <summary>
/// Transport-safe representation of one Adaptive Anomaly Spotlight issue.
///
/// Domain enums are exposed as strings so the Contracts project remains
/// independent from SmartX.Domain.
/// </summary>
public sealed record DashboardIssueResponse(
    Guid Id,
    Guid SensorProfileId,
    string SensorName,
    string Type,
    string Severity,
    string State,
    string Target,
    string Title,
    string Detail,
    int OccurrenceCount,
    double PriorityScore,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ResolvedAtUtc);