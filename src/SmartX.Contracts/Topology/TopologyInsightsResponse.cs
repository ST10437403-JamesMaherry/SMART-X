namespace SmartX.Contracts.Topology;

/// <summary>
/// A bounded, read-only operational interpretation of one authoritative mesh
/// revision. These are rule-derived facts, NOT historically learned commands.
/// Both times are UTC: EvaluatedAtUtc is the HTTP read time; the projection
/// timestamp changes only when the source graph revision is recomputed.
/// </summary>
public sealed record TopologyInsightsResponse(
    long GraphVersion,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset ProjectionComputedAtUtc,
    int RegisteredSensorCount,
    int ReachableSensorCount,
    int DisconnectedSensorCount,
    int UnavailableSensorCount,
    IReadOnlyList<TopologyInsightResponse> Insights);

/// <summary>
/// One explainable topology condition or a short-lived recovery observation.
/// Stable Key values coalesce repeated reads and graph revisions. IsCurrent
/// distinguishes live problems from historical recovery announcements.
/// AffectedSensorIds contains real registry identities found in the graph,
/// capped to keep network payload sizes independent of mesh size.
/// </summary>
public sealed record TopologyInsightResponse(
    string Key,
    string Kind,
    string Severity,
    string Source,
    bool IsLearned,
    bool IsCurrent,
    string Title,
    string Explanation,
    string RecommendedAction,
    long EvidenceGraphVersion,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastConfirmedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    int AffectedRegisteredSensorCount,
    IReadOnlyList<Guid> AffectedSensorIds,
    int AdditionalAffectedSensorCount);