namespace SmartX.Contracts.Recommendations;

/// <summary>
/// Transport-only description of one server-evaluated, unexecuted suggestion.
/// All identities are stable GUIDs; the displayed names may change without
/// affecting how the prediction pattern was learned.
/// </summary>
public sealed record PredictedRecommendationResponse(
    Guid SourceAlertEpisodeId,
    Guid SourceDeviceId,
    string SourceDeviceName,
    string ConditionType,
    string Severity,
    string Movement,
    Guid TargetActuatorId,
    string TargetActuatorName,
    string CommandType,
    int SupportingEpisodes,
    DateTimeOffset LastLearnedAtUtc,
    int Score,
    string ConfidenceBand,
    DateTimeOffset ExpiresAtUtc,
    string Explanation);

/// <summary>
/// One bounded, read-only prediction snapshot. All recommended actions must
/// still be explicitly selected and revalidated by the normal command API.
/// NextRefreshAtUtc is advisory: it gives the earliest known eligibility
/// expiry; clients must also refresh after SignalR/reconnect and periodically
/// while visible because an EMPTY result cannot provide an expiry deadline.
/// </summary>
public sealed record PredictedRecommendationsResponse(
    DateTimeOffset EvaluatedAtUtc,
    int MinimumSupportingEpisodes,
    double EvidenceWindowSeconds,
    DateTimeOffset? NextRefreshAtUtc,
    IReadOnlyList<PredictedRecommendationResponse> Recommendations);