using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Recommendations;

namespace SmartX.Application.Recommendations;

/// <summary>
/// An intentionally qualitative interpretation of a bounded heuristic score.
/// These bands are not calibrated probabilities of command success.
/// </summary>
public enum PredictionConfidenceBand
{
    Emerging = 1,
    Moderate = 2,
    High = 3
}

/// <summary>
/// An immutable, read-only suggested action for one CURRENT alert episode.
/// The identifiers, target eligibility, and relay state have been checked
/// against the live application stores. Actual execution must independently
/// revalidate the target through the existing ActuatorCommandService.
/// </summary>
public sealed record PredictedAction(
    Guid SourceAlertEpisodeId,
    Guid SourceDeviceId,
    string SourceDeviceName,
    DashboardIssueType ConditionType,
    DashboardIssueSeverity Severity,
    TelemetryMovementDirection Movement,
    Guid TargetActuatorId,
    string TargetActuatorName,
    RelayCommandType Command,
    int SupportingEpisodes,
    DateTimeOffset LastLearnedAtUtc,
    int Score,
    PredictionConfidenceBand ConfidenceBand,
    DateTimeOffset ExpiresAtUtc,
    string Explanation);

/// <summary>
/// A detached read of the current explainable recommendations. Empty results
/// indicate insufficient eligible history or absent current conditions, not
/// a client-side fallback to invented suggestions.
/// </summary>
public sealed record PredictedActionSnapshot(
    DateTimeOffset EvaluatedAtUtc,
    int MinimumSupportingEpisodes,
    TimeSpan EvidenceWindow,
    IReadOnlyList<PredictedAction> Actions);