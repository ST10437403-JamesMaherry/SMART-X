using SmartX.Application.Recommendations;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Recommendations;

namespace SmartX.Infrastructure.Recommendations;

/// <summary>
/// One process-local Dictionary&lt;InteractionPatternKey, PatternBucket&gt; stores
/// pattern support without replacing the P2-08 operator interaction stream.
///
/// The lock covers deduplication, expiry, bounded eviction and snapshots.
/// Support is the count of independent active episodes still inside the
/// configured evidence window; it cannot be increased by repeated clicks in
/// one episode, nor by repeatedly reading this projection.
/// </summary>
public sealed class InMemoryInteractionPatternStore : IInteractionPatternStore
{
    private sealed class PatternBucket
    {
        public Dictionary<Guid, PatternEpisodeEvidence> Episodes { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<InteractionPatternKey, PatternBucket> _patterns = [];
    private readonly TimeProvider _clock;
    private readonly PatternLearningPolicy _policy;

    public InMemoryInteractionPatternStore(TimeProvider clock, PatternLearningPolicy policy)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public bool Observe(InteractionPatternObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateObservation(observation);

        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
        PatternEpisodeEvidence evidence = observation.Evidence;

        lock (_gate)
        {
            PruneExpired(now);

            // Evidence outside the time window is not training data. Future
            // timestamps are rejected rather than counted as recent support.
            if (evidence.ObservedAtUtc > now ||
                evidence.ObservedAtUtc < now - _policy.EvidenceWindow)
            {
                return false;
            }

            if (!_patterns.TryGetValue(observation.Key, out PatternBucket? bucket))
            {
                if (_patterns.Count == _policy.MaximumPatterns)
                {
                    // Deterministic least-recently-observed eviction. A busy
                    // simulator cannot cause the pattern dictionary to grow.
                    InteractionPatternKey oldest = _patterns
                        .OrderBy(pair => pair.Value.Episodes.Values
                            .Max(entry => entry.ObservedAtUtc))
                        .ThenBy(pair => pair.Key.SourceDeviceId)
                        .ThenBy(pair => pair.Key.ConditionType)
                        .ThenBy(pair => pair.Key.Severity)
                        .ThenBy(pair => pair.Key.Movement)
                        .ThenBy(pair => pair.Key.TargetActuatorId)
                        .ThenBy(pair => pair.Key.Command)
                        .First().Key;
                    _patterns.Remove(oldest);
                }

                bucket = new PatternBucket();
                _patterns.Add(observation.Key, bucket);
            }

            if (bucket.Episodes.ContainsKey(evidence.AlertEpisodeId))
                return false;

            // Do not evict one episode merely to count a repeated click on
            // that same episode again. Stop admitting evidence until expiry;
            // retained support therefore remains truthful and bounded.
            if (bucket.Episodes.Count == _policy.MaximumEpisodesPerPattern)
                return false;

            bucket.Episodes.Add(evidence.AlertEpisodeId, evidence);
            return true;
        }
    }

    public IReadOnlyList<PatternStatistics> GetSnapshot()
    {
        lock (_gate)
        {
            PruneExpired(_clock.GetUtcNow().ToUniversalTime());

            PatternStatistics[] snapshot = _patterns.Select(pair =>
            {
                PatternEpisodeEvidence[] episodes = pair.Value.Episodes.Values
                    .OrderBy(item => item.ObservedAtUtc)
                    .ThenBy(item => item.AlertEpisodeId)
                    .ToArray();

                return new PatternStatistics(pair.Key, episodes.Length,
                    episodes[^1].ObservedAtUtc,
                    Array.AsReadOnly(episodes));
            })
                .OrderByDescending(item => item.SupportingEpisodes)
                .ThenByDescending(item => item.LastObservedAtUtc)
                .ThenBy(item => item.Key.SourceDeviceId)
                .ThenBy(item => item.Key.ConditionType)
                .ThenBy(item => item.Key.Severity)
                .ThenBy(item => item.Key.Movement)
                .ThenBy(item => item.Key.TargetActuatorId)
                .ThenBy(item => item.Key.Command)
                .ToArray();

            return Array.AsReadOnly(snapshot);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _policy.EvidenceWindow;

        foreach (InteractionPatternKey key in _patterns.Keys.ToArray())
        {
            PatternBucket bucket = _patterns[key];
            foreach (Guid episodeId in bucket.Episodes
                .Where(pair => pair.Value.ObservedAtUtc < cutoff)
                .Select(pair => pair.Key).ToArray())
            {
                bucket.Episodes.Remove(episodeId);
            }

            if (bucket.Episodes.Count == 0)
                _patterns.Remove(key);
        }
    }

    private static void ValidateObservation(InteractionPatternObservation observation)
    {
        InteractionPatternKey key = observation.Key
            ?? throw new ArgumentException("A pattern key is required.", nameof(observation));
        PatternEpisodeEvidence evidence = observation.Evidence
            ?? throw new ArgumentException("Verified episode evidence is required.", nameof(observation));

        if (key.SourceDeviceId == Guid.Empty || key.TargetActuatorId == Guid.Empty ||
            key.SourceDeviceId == key.TargetActuatorId ||
            evidence.AlertEpisodeId == Guid.Empty || evidence.CommandId == Guid.Empty ||
            evidence.ObservedAtUtc == default ||
            !Enum.IsDefined(key.ConditionType) || !Enum.IsDefined(key.Severity) ||
            !Enum.IsDefined(key.Movement) || !Enum.IsDefined(key.Command))
        {
            throw new ArgumentException("Invalid source, target, condition, command or episode evidence.",
                nameof(observation));
        }

        if ((key.ConditionType == DashboardIssueType.TelemetrySpike &&
                key.Movement == TelemetryMovementDirection.NotApplicable) ||
            (key.ConditionType != DashboardIssueType.TelemetrySpike &&
                key.Movement != TelemetryMovementDirection.NotApplicable))
        {
            throw new ArgumentException("Numeric movement must match the source condition type.",
                nameof(observation));
        }
    }
}