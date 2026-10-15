namespace SmartX.Application.Recommendations;

/// <summary>
/// Shared bounds for server-validated learning and read-only recommendation
/// ranking. All time-dependent decisions use the same injected TimeProvider.
///
/// A recommendation needs at least three distinct successful-response
/// episodes within the evidence window; a single repeatedly clicked alert
/// is never sufficient. These rules are heuristic, not machine-learning
/// probabilities or claims about authenticated individual operators.
/// </summary>
public sealed class PatternLearningPolicy
{
    public PatternLearningPolicy(
        TimeSpan? evidenceWindow = null,
        TimeSpan? maximumInteractionSpan = null,
        int maximumPatterns = 512,
        int maximumEpisodesPerPattern = 256,
        int minimumSupportingEpisodes = 3,
        TimeSpan? maximumCurrentConditionAge = null,
        int maximumRecommendations = 20)
    {
        EvidenceWindow = evidenceWindow ?? TimeSpan.FromDays(7);
        MaximumInteractionSpan = maximumInteractionSpan ?? TimeSpan.FromMinutes(15);
        MaximumCurrentConditionAge = maximumCurrentConditionAge ?? TimeSpan.FromDays(1);

        if (EvidenceWindow <= TimeSpan.Zero || MaximumInteractionSpan <= TimeSpan.Zero ||
            MaximumInteractionSpan > EvidenceWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceWindow),
                "Learning and interaction windows must be positive; the interaction window cannot exceed retention.");
        }

        if (MaximumCurrentConditionAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCurrentConditionAge),
                "A live condition must have a positive freshness window.");
        }

        if (maximumPatterns < 1 || maximumEpisodesPerPattern < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPatterns),
                "Pattern and per-pattern episode capacities must be positive.");
        }

        if (minimumSupportingEpisodes < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSupportingEpisodes),
                "A recommendation requires at least three independent alert episodes.");
        }

        if (maximumRecommendations < 1 || maximumRecommendations > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecommendations),
                "The returned recommendation limit must be between 1 and 100.");
        }

        MaximumPatterns = maximumPatterns;
        MaximumEpisodesPerPattern = maximumEpisodesPerPattern;
        MinimumSupportingEpisodes = minimumSupportingEpisodes;
        MaximumRecommendations = maximumRecommendations;
    }

    /// <summary>Maximum age of training evidence; the default is seven days.</summary>
    public TimeSpan EvidenceWindow { get; }

    /// <summary>Maximum span between alert inspection and command execution.</summary>
    public TimeSpan MaximumInteractionSpan { get; }

    /// <summary>Maximum age of the last observation for a live source issue.</summary>
    public TimeSpan MaximumCurrentConditionAge { get; }

    /// <summary>Number of independent episodes required before suggesting.</summary>
    public int MinimumSupportingEpisodes { get; }

    /// <summary>Maximum distinct keys stored in the process-local Dictionary.</summary>
    public int MaximumPatterns { get; }

    /// <summary>Maximum independent episodes retained for a single key.</summary>
    public int MaximumEpisodesPerPattern { get; }

    /// <summary>Maximum explained recommendations returned by one read.</summary>
    public int MaximumRecommendations { get; }
}