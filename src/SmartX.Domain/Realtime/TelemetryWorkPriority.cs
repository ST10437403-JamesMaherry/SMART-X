using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Realtime;

/// <summary>Arrival order breaks ties explicitly; PriorityQueue is not stable itself.</summary>
public readonly record struct TelemetryWorkPriority(
    DashboardIssueSeverity Severity,
    long ArrivalSequence);

/// <summary>
/// .NET removes the smallest priority first. Reverse severity, then retain
/// ascending arrival order. CompareTo avoids subtraction and overflow.
/// </summary>
public sealed class TelemetryWorkPriorityComparer : IComparer<TelemetryWorkPriority>
{
    public static TelemetryWorkPriorityComparer Instance { get; } = new();

    private TelemetryWorkPriorityComparer()
    {
    }

    public int Compare(TelemetryWorkPriority x, TelemetryWorkPriority y)
    {
        var severityOrder = y.Severity.CompareTo(x.Severity);
        return severityOrder != 0
            ? severityOrder
            : x.ArrivalSequence.CompareTo(y.ArrivalSequence);
    }
}