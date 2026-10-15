namespace SmartX.Application.Recommendations;

/// <summary>
/// Explicit, bounded learning rules. Seven-day expiry and a 15-minute
/// alert-to-command association are defaults, not hidden hard-coded clocks.
/// Minimum recommendation support and ranking are introduced in P2-09 Commit 2.
/// </summary>
public sealed class PatternLearningPolicy
{
    public PatternLearningPolicy(
        TimeSpan? evidenceWindow = null,
        TimeSpan? maximumInteractionSpan = null,
        int maximumPatterns = 512,
        int maximumEpisodesPerPattern = 256)
    {
        EvidenceWindow = evidenceWindow ?? TimeSpan.FromDays(7);
        MaximumInteractionSpan = maximumInteractionSpan ?? TimeSpan.FromMinutes(15);

        if (EvidenceWindow <= TimeSpan.Zero || MaximumInteractionSpan <= TimeSpan.Zero ||
            MaximumInteractionSpan > EvidenceWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceWindow),
                "Learning and interaction windows must be positive; the interaction window cannot exceed retention.");
        }

        if (maximumPatterns < 1 || maximumEpisodesPerPattern < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPatterns),
                "Pattern and per-pattern episode capacities must be positive.");
        }

        MaximumPatterns = maximumPatterns;
        MaximumEpisodesPerPattern = maximumEpisodesPerPattern;
    }

    public TimeSpan EvidenceWindow { get; }

    public TimeSpan MaximumInteractionSpan { get; }

    public int MaximumPatterns { get; }

    public int MaximumEpisodesPerPattern { get; }
}