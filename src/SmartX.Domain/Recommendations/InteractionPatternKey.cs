using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Recommendations;

/// <summary>
/// The movement of the numeric observation behind a telemetry-spike episode.
/// A decrease and an increase must never train or match the same pattern.
/// Other operational conditions have no numeric movement to classify.
/// </summary>
public enum TelemetryMovementDirection
{
    NotApplicable = 0,
    Increase = 1,
    Decrease = 2
}

/// <summary>
/// Stable identity of one learned operator response. GUIDs are authoritative
/// source/target identities; mutable sensor names are never part of this key.
/// </summary>
public sealed record InteractionPatternKey(
    Guid SourceDeviceId,
    DashboardIssueType ConditionType,
    DashboardIssueSeverity Severity,
    TelemetryMovementDirection Movement,
    Guid TargetActuatorId,
    RelayCommandType Command);