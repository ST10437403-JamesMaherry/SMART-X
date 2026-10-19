using SmartX.Application.Dashboard;
using SmartX.Application.Recommendations;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Interactions;
using SmartX.Domain.Recommendations;
using SmartX.Infrastructure.Interactions;
using SmartX.Infrastructure.Recommendations;

namespace SmartX.Infrastructure.Tests.Recommendations;

/// <summary>
/// Correlates real P2-08 store events, the P2-06 active-issue collection, and
/// authoritative command records. No injected pattern count is used.
/// </summary>
public sealed class PredictivePatternLearningServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 15, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AlertViewSearchAndAcceptedCommand_LearnsOnlyOncePerEpisode()
    {
        var fixture = new Fixture();
        Guid episode = fixture.ReportSpike("relative decrease");
        fixture.Associate(episode);
        CommandRecord on = fixture.SuccessfulCommand(RelayCommandType.RelayOn);

        Assert.True(fixture.Learner.ObserveAcceptedCommand(on));
        Assert.False(fixture.Learner.ObserveAcceptedCommand(on));
        Assert.Equal(1, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
        Assert.Equal(TelemetryMovementDirection.Decrease,
            Assert.Single(fixture.Patterns.GetSnapshot()).Key.Movement);
    }

    [Fact]
    public void IncreasingAndDecreasingSourceEpisodes_NeverSharePattern()
    {
        var fixture = new Fixture();
        Guid drop = fixture.ReportSpike("relative decrease");
        fixture.Associate(drop);
        Assert.True(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));

        Assert.True(fixture.Issues.Resolve(fixture.SourceId,
            DashboardIssueType.TelemetrySpike, fixture.Clock.UtcNow));
        Guid rise = fixture.ReportSpike("relative increase");
        fixture.Associate(rise);
        Assert.True(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));

        Assert.Equal(2, fixture.Patterns.GetSnapshot().Count);
        Assert.Contains(fixture.Patterns.GetSnapshot(), p =>
            p.Key.Movement == TelemetryMovementDirection.Decrease);
        Assert.Contains(fixture.Patterns.GetSnapshot(), p =>
            p.Key.Movement == TelemetryMovementDirection.Increase);
    }

    [Fact]
    public void WithoutAlertViewOrAssociation_NoPatternIsLearned()
    {
        var fixture = new Fixture();
        Guid episode = fixture.ReportSpike("relative decrease");
        fixture.Interactions.Append(OperatorInteractionKind.SearchSubmitted,
            deviceId: fixture.TargetId, alertEpisodeId: episode);
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));
        Assert.Empty(fixture.Patterns.GetSnapshot());
    }

    [Fact]
    public void UnrelatedLaterSearch_PreventsAssociationReuse()
    {
        var fixture = new Fixture();
        fixture.Associate(fixture.ReportSpike("relative decrease"));
        fixture.Interactions.Append(OperatorInteractionKind.SearchSubmitted,
            deviceId: Guid.NewGuid());
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));
        Assert.Empty(fixture.Patterns.GetSnapshot());
    }

    [Fact]
    public void NoOpUndoAndRecoveredAlert_DoNotTeachPatterns()
    {
        var fixture = new Fixture();
        fixture.Associate(fixture.ReportSpike("relative decrease"));
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.Command(RelayCommandType.RelayOff, RelayState.Off, RelayState.Off)));
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.Command(RelayCommandType.RelayOff, RelayState.On, RelayState.Off,
                reversesCommandId: Guid.NewGuid())));
        Assert.True(fixture.Issues.Resolve(fixture.SourceId,
            DashboardIssueType.TelemetrySpike, fixture.Clock.UtcNow));
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));
        Assert.Empty(fixture.Patterns.GetSnapshot());
    }

    [Fact]
    public void ExpiredAssociationAndAmbiguousMovement_AreIgnored()
    {
        var fixture = new Fixture();
        fixture.Associate(fixture.ReportSpike("relative decrease"));
        fixture.Clock.UtcNow = BaseTime.AddMinutes(16);
        Assert.False(fixture.Learner.ObserveAcceptedCommand(
            fixture.SuccessfulCommand(RelayCommandType.RelayOn)));
        Assert.Empty(fixture.Patterns.GetSnapshot());

        var ambiguous = new Fixture();
        ambiguous.Associate(ambiguous.ReportSpike("changed unexpectedly"));
        Assert.False(ambiguous.Learner.ObserveAcceptedCommand(
            ambiguous.SuccessfulCommand(RelayCommandType.RelayOn)));
    }

    private sealed class Fixture
    {
        public MutableTimeProvider Clock { get; } = new(BaseTime);

        public Guid SourceId { get; } = Guid.NewGuid();

        public Guid TargetId { get; } = Guid.NewGuid();

        public DashboardIssueCollection Issues { get; } = new();

        public InMemoryOperatorInteractionStore Interactions { get; }

        public InMemoryInteractionPatternStore Patterns { get; }

        public PredictivePatternLearningService Learner { get; }

        public Fixture()
        {
            var policy = new PatternLearningPolicy();
            Interactions = new InMemoryOperatorInteractionStore(Clock);
            Patterns = new InMemoryInteractionPatternStore(Clock, policy);
            Learner = new PredictivePatternLearningService(Interactions,
                new DashboardIssueService(Issues, Clock), Patterns, policy, Clock);
        }

        public Guid ReportSpike(string movement)
        {
            DashboardIssue issue = Issues.AddOrUpdate(SourceId, "Evidence Source",
                DashboardIssueType.TelemetrySpike, DashboardIssueSeverity.Critical,
                DashboardIssueTarget.Telemetry, "Critical telemetry spike",
                $"Telemetry changed from 50 to 10, a 80% {movement}, over 3 seconds.", Clock.UtcNow);
            return issue.Id;
        }

        public void Associate(Guid episodeId)
        {
            Interactions.Append(OperatorInteractionKind.AlertViewed,
                deviceId: SourceId, alertEpisodeId: episodeId);
            Interactions.Append(OperatorInteractionKind.SearchSubmitted,
                deviceId: TargetId, alertEpisodeId: episodeId);
        }

        public CommandRecord Command(RelayCommandType type, RelayState previous,
            RelayState resulting, Guid? reversesCommandId = null)
        {
            var record = new CommandRecord(Guid.NewGuid(), TargetId, type, Clock.UtcNow,
                previous, resulting, reversesCommandId);
            Interactions.Append(reversesCommandId.HasValue
                ? OperatorInteractionKind.CommandUndone
                : OperatorInteractionKind.CommandExecuted,
                deviceId: TargetId, commandId: record.Id);
            return record;
        }

        public CommandRecord SuccessfulCommand(RelayCommandType type) =>
            Command(type, RelayState.Off, RelayState.On);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}