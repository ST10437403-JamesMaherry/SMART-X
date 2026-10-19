namespace SmartX.Contracts.Realtime;

/// <summary>
/// Read-only representation of one currently unresolved Smart-X condition.
///
/// The membership key corresponds to the canonical sensor-plus-condition
/// identity used by the Domain HashSet. A repeated observation preserves
/// both MembershipKey and IssueId until recovery.
///
/// Acknowledgement changes operator state but does not remove membership.
/// Following recovery, a later recurrence creates a new IssueId while
/// reusing the same stable MembershipKey.
/// </summary>
public sealed record RealtimeActiveConditionResponse(
    string MembershipKey,
    Guid IssueId,
    Guid SensorProfileId,
    string ConditionType,
    string Severity,
    string State,
    int OccurrenceCount,
    double PriorityScore,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

/// <summary>
/// Authoritative, detached active-condition snapshot for Part 2.
///
/// Counts and entries come from one existing Adaptive Anomaly Spotlight
/// snapshot. The response does not own or persist any condition state.
///
/// ActiveConditionCount includes Active and Acknowledged conditions.
/// UnacknowledgedConditionCount includes only conditions requiring
/// operator acknowledgement.
///
/// Resolved issues remain accessible through the original
/// GET /api/dashboard/issues?includeResolved=true endpoint.
/// </summary>
public sealed record RealtimeActiveConditionsResponse(
    DateTimeOffset GeneratedAtUtc,
    int ActiveConditionCount,
    int UnacknowledgedConditionCount,
    IReadOnlyList<RealtimeActiveConditionResponse> ActiveConditions);