using SmartX.Domain.Dashboard;

namespace SmartX.Application.Dashboard;

/// <summary>
/// Pure classification of one exact packet pair. HasEvidence distinguishes
/// stable telemetry from a missing baseline or a back-filled observation.
/// </summary>
public sealed record TelemetryAnomalyObservation(
    bool HasEvidence,
    bool IsAnomaly,
    DashboardIssueSeverity Severity,
    string? Detail)
{
    public static TelemetryAnomalyObservation None { get; } =
        new(false, false, DashboardIssueSeverity.Information, null);

    public static TelemetryAnomalyObservation Stable { get; } =
        new(true, false, DashboardIssueSeverity.Information, null);
}