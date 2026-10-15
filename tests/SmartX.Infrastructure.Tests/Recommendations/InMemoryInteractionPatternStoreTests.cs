using SmartX.Application.Recommendations;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Recommendations;
using SmartX.Infrastructure.Recommendations;

namespace SmartX.Infrastructure.Tests.Recommendations;

/// <summary>
/// Unit tests for the actual process-local Dictionary projection. Evidence is
/// constructed as explicit test fixtures; production records these facts only
/// after its real command and interaction validation path succeeds.
/// </summary>
public sealed class InMemoryInteractionPatternStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observe_CountsUniqueEpisodesAndDoesNotInflateOnRepeatedClicks()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var store = new InMemoryInteractionPatternStore(clock, new PatternLearningPolicy());
        InteractionPatternKey key = Key();
        Guid episode = Guid.NewGuid();

        Assert.True(store.Observe(Evidence(key, episode, BaseTime)));
        Assert.False(store.Observe(Evidence(key, episode, BaseTime)));
        Assert.True(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime)));
        Assert.True(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime)));

        PatternStatistics pattern = Assert.Single(store.GetSnapshot());
        Assert.Equal(3, pattern.SupportingEpisodes);
        Assert.Equal(3, pattern.Episodes.Select(x => x.AlertEpisodeId).Distinct().Count());
        Assert.All(pattern.Episodes, x => Assert.Equal(TimeSpan.Zero, x.ObservedAtUtc.Offset));
        Assert.Equal(3, Assert.Single(store.GetSnapshot()).SupportingEpisodes);
    }

    [Fact]
    public void Keys_SeparateDirectionConditionSeverityActuatorAndCommand()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var store = new InMemoryInteractionPatternStore(clock, new PatternLearningPolicy());
        InteractionPatternKey baseKey = Key();

        InteractionPatternKey[] keys =
        [
            baseKey,
            baseKey with { Movement = TelemetryMovementDirection.Increase },
            baseKey with { ConditionType = DashboardIssueType.SensorDisconnected,
                Movement = TelemetryMovementDirection.NotApplicable },
            baseKey with { Severity = DashboardIssueSeverity.Warning },
            baseKey with { TargetActuatorId = Guid.NewGuid() },
            baseKey with { Command = RelayCommandType.RelayOff }
        ];

        foreach (InteractionPatternKey key in keys)
            Assert.True(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime)));

        Assert.Equal(keys.Length, store.GetSnapshot().Count);
        Assert.All(store.GetSnapshot(), item => Assert.Equal(1, item.SupportingEpisodes));
    }

    [Fact]
    public void Snapshot_PrunesSevenDayOldEvidenceWithoutReadSideLearning()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var policy = new PatternLearningPolicy(evidenceWindow: TimeSpan.FromDays(7));
        var store = new InMemoryInteractionPatternStore(clock, policy);
        InteractionPatternKey key = Key();

        Assert.True(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime)));
        clock.UtcNow = BaseTime.AddDays(7);
        Assert.Single(store.GetSnapshot()); // The exact seven-day boundary is retained.

        clock.UtcNow = BaseTime.AddDays(7).AddTicks(1);
        Assert.Empty(store.GetSnapshot());
        Assert.False(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime)));
    }

    [Fact]
    public void Capacity_BoundsPatternsAndEpisodesWithoutReplayingOldEvidence()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var policy = new PatternLearningPolicy(maximumPatterns: 2, maximumEpisodesPerPattern: 2);
        var store = new InMemoryInteractionPatternStore(clock, policy);
        InteractionPatternKey first = Key();
        Guid firstEpisode = Guid.NewGuid();

        Assert.True(store.Observe(Evidence(first, firstEpisode, BaseTime)));
        Assert.True(store.Observe(Evidence(first, Guid.NewGuid(), BaseTime)));
        Assert.False(store.Observe(Evidence(first, firstEpisode, BaseTime)));
        Assert.False(store.Observe(Evidence(first, Guid.NewGuid(), BaseTime)));
        Assert.Equal(2, Assert.Single(store.GetSnapshot()).SupportingEpisodes);

        InteractionPatternKey second = first with { TargetActuatorId = Guid.NewGuid() };
        InteractionPatternKey third = first with { TargetActuatorId = Guid.NewGuid() };
        Assert.True(store.Observe(Evidence(second, Guid.NewGuid(), BaseTime)));
        clock.UtcNow = BaseTime.AddSeconds(1);
        Assert.True(store.Observe(Evidence(third, Guid.NewGuid(), clock.UtcNow)));

        Assert.Equal(2, store.GetSnapshot().Count);
        Assert.Contains(store.GetSnapshot(), pattern => pattern.Key == third);
    }

    [Fact]
    public async Task ConcurrentObservations_CountEachEpisodeOnce()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var store = new InMemoryInteractionPatternStore(clock,
            new PatternLearningPolicy(maximumEpisodesPerPattern: 100));
        InteractionPatternKey key = Key();
        Guid[] episodes = Enumerable.Range(0, 70).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 350).Select(i => Task.Run(() =>
            store.Observe(Evidence(key, episodes[i % episodes.Length], BaseTime)))));

        Assert.Equal(70, Assert.Single(store.GetSnapshot()).SupportingEpisodes);
    }

    [Fact]
    public void InvalidOrFutureEvidence_IsNotAccepted()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var store = new InMemoryInteractionPatternStore(clock, new PatternLearningPolicy());
        InteractionPatternKey key = Key();
        Assert.False(store.Observe(Evidence(key, Guid.NewGuid(), BaseTime.AddSeconds(1))));
        Assert.Throws<ArgumentException>(() => store.Observe(Evidence(
            key with { Movement = TelemetryMovementDirection.NotApplicable }, Guid.NewGuid(), BaseTime)));
        Assert.Throws<ArgumentException>(() => store.Observe(Evidence(
            key with { SourceDeviceId = key.TargetActuatorId }, Guid.NewGuid(), BaseTime)));
        Assert.Empty(store.GetSnapshot());
    }

    private static InteractionPatternKey Key() =>
        new(Guid.NewGuid(), DashboardIssueType.TelemetrySpike,
            DashboardIssueSeverity.Critical, TelemetryMovementDirection.Decrease,
            Guid.NewGuid(), RelayCommandType.RelayOn);

    private static InteractionPatternObservation Evidence(
        InteractionPatternKey key, Guid episode, DateTimeOffset atUtc) =>
        new(key, new PatternEpisodeEvidence(episode, Guid.NewGuid(), atUtc));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}