namespace SmartX.Domain.Recommendations;

/// <summary>
/// One independently verified condition episode followed by a successful,
/// state-changing manual command. Source episode IDs are deduplication keys.
/// </summary>
public sealed record PatternEpisodeEvidence(
    Guid AlertEpisodeId,
    Guid CommandId,
    DateTimeOffset ObservedAtUtc);

/// <summary>Immutable observation delivered only by the server command path.</summary>
public sealed record InteractionPatternObservation(
    InteractionPatternKey Key,
    PatternEpisodeEvidence Evidence);

/// <summary>
/// Detached statistics from the bounded Dictionary-backed projection.
/// SupportingEpisodes describes distinct source-alert episodes still retained
/// inside the policy window; it is not a probability or a persisted profile.
/// </summary>
public sealed record PatternStatistics(
    InteractionPatternKey Key,
    int SupportingEpisodes,
    DateTimeOffset LastObservedAtUtc,
    IReadOnlyList<PatternEpisodeEvidence> Episodes);