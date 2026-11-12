using SmartX.Application.Commands;
using SmartX.Application.Dashboard;
using SmartX.Application.Recommendations;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Interactions;
using SmartX.Domain.Recommendations;
using SmartX.Domain.Sensors;
using SmartX.Infrastructure.Commands;
using SmartX.Infrastructure.Interactions;
using SmartX.Infrastructure.Recommendations;
using SmartX.Infrastructure.Sensors;

namespace SmartX.Infrastructure.Tests.Recommendations;

/// <summary>
/// P3-10 baseline: protect the Part 2 learning/command boundary before adding
/// rule-derived topology insights. These tests construct the actual registered
/// sensor, active-issue, operator-event, pattern and Stack-backed command stores.
/// No test injects a fake supporting-episode count or mocks a successful command.
/// </summary>
public sealed class P3_10PredictionEvidenceBoundaryTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OrdinaryDeviceViewWithAlertAssociatedSearch_DoesNotTrainUntilAlertIsViewed()
    {
        Fixture fixture = await Fixture.CreateAsync();
        Guid episode = fixture.OpenFreshSpikeEpisode();

        // A search can name an authentic active episode, but a generic device
        // view is not the deliberate AlertViewed evidence required by learning.
        fixture.Interactions.Append(OperatorInteractionKind.DeviceViewed,
            deviceId: fixture.Source.Id);
        fixture.SelectActuator(episode);

        ActuatorCommandExecutionResult first = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);
        Assert.True(first.StateChanged);
        Assert.Empty(fixture.Patterns.GetSnapshot());
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        // Undo changes the command Stack, not the learned episode count.
        ActuatorCommandExecutionResult reversal = await fixture.Commands.UndoAsync(
            fixture.Target.Id, first.Command.Id);
        Assert.True(reversal.IsReversal);
        Assert.Empty(fixture.Patterns.GetSnapshot());

        // Only a new, explicit alert view followed by a selected search and
        // another genuinely accepted state change can teach this episode.
        fixture.ViewAlert(episode);
        fixture.SelectActuator(episode);
        ActuatorCommandExecutionResult accepted = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);

        Assert.True(accepted.StateChanged);
        PatternStatistics learned = Assert.Single(fixture.Patterns.GetSnapshot());
        Assert.Equal(1, learned.SupportingEpisodes);
        Assert.Equal(episode, Assert.Single(learned.Episodes).AlertEpisodeId);
        Assert.Equal(accepted.Command.Id, learned.Episodes[0].CommandId);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task InventedCommandRecordWithoutServerExecutionAudit_CannotBecomeEvidence()
    {
        Fixture fixture = await Fixture.CreateAsync();
        Guid episode = fixture.OpenFreshSpikeEpisode();
        fixture.ViewAlert(episode);
        fixture.SelectActuator(episode);

        // This resembles a successful relay command but was NOT committed by
        // ActuatorCommandService and has no matching CommandExecuted event.
        var invented = new CommandRecord(Guid.NewGuid(), fixture.Target.Id,
            RelayCommandType.RelayOn, fixture.Clock.UtcNow,
            RelayState.Off, RelayState.On);

        Assert.False(fixture.Learner.ObserveAcceptedCommand(invented));
        Assert.Empty(fixture.Patterns.GetSnapshot());
        Assert.Equal(RelayState.Off,
            (await fixture.CommandStore.GetSnapshotAsync(fixture.Target.Id)).CurrentState);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        // The unchanged production path DOES learn after the server's real
        // command transaction and matching interaction record succeed.
        ActuatorCommandExecutionResult real = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);
        Assert.True(real.StateChanged);
        Assert.NotEqual(invented.Id, real.Command.Id);
        Assert.Equal(real.Command.Id,
            Assert.Single(Assert.Single(fixture.Patterns.GetSnapshot()).Episodes).CommandId);
    }

    [Fact]
    public async Task SameTimestampInteractions_RequireTheLatestSelectedTarget()
    {
        Fixture fixture = await Fixture.CreateAsync();
        SensorProfile other = await fixture.RegisterActuatorAsync("Secondary relay");
        Guid episode = fixture.OpenFreshSpikeEpisode();
        fixture.ViewAlert(episode);

        // All events deliberately share the same UTC time. Server-assigned
        // sequences, not timestamps or a browser-side filter, determine order.
        fixture.SelectActuator(episode);
        fixture.Interactions.Append(OperatorInteractionKind.SearchSubmitted,
            deviceId: other.Id, alertEpisodeId: episode);
        ActuatorCommandExecutionResult first = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);

        Assert.Empty(fixture.Patterns.GetSnapshot());
        await fixture.Commands.UndoAsync(fixture.Target.Id, first.Command.Id);

        fixture.SelectActuator(episode);
        ActuatorCommandExecutionResult second = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);

        PatternStatistics learned = Assert.Single(fixture.Patterns.GetSnapshot());
        Assert.Equal(fixture.Target.Id, learned.Key.TargetActuatorId);
        Assert.Equal(episode, Assert.Single(learned.Episodes).AlertEpisodeId);
        Assert.Equal(second.Command.Id, learned.Episodes[0].CommandId);
        Assert.All(fixture.Interactions.Read().Events,
            item => Assert.Equal(Anchor, item.OccurredAtUtc));
    }

    [Fact]
    public async Task ThreeRealEpisodesProduceAdvisoryOnly_AndRecoveryWithdrawsAdvice()
    {
        Fixture fixture = await Fixture.CreateAsync();
        var recordedEpisodes = new HashSet<Guid>();

        for (int count = 1; count <= 3; count++)
        {
            Guid episode = fixture.OpenFreshSpikeEpisode();
            Assert.True(recordedEpisodes.Add(episode));
            fixture.ViewAlert(episode);
            fixture.SelectActuator(episode);

            ActuatorCommandExecutionResult command = await fixture.Commands.ExecuteAsync(
                fixture.Target.Id, RelayCommandType.RelayOn);
            Assert.True(command.StateChanged);

            // Undo restores the live command state so the next distinct
            // episode can genuinely accept a new state-changing command.
            ActuatorCommandExecutionResult undone = await fixture.Commands.UndoAsync(
                fixture.Target.Id, command.Command.Id);
            Assert.True(undone.IsReversal);
            Assert.Equal(RelayState.Off, undone.CurrentState);

            Assert.Equal(count, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
            if (count < 3)
            {
                Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
            }
        }

        PredictedAction suggestion = Assert.Single(
            (await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(3, suggestion.SupportingEpisodes);
        Assert.Equal(85, suggestion.Score);
        Assert.Equal(PredictionConfidenceBand.High, suggestion.ConfidenceBand);
        Assert.Equal(fixture.Source.Id, suggestion.SourceDeviceId);
        Assert.Equal(fixture.Target.Id, suggestion.TargetActuatorId);
        Assert.Equal(RelayCommandType.RelayOn, suggestion.Command);
        Assert.Contains("3 distinct alert episodes", suggestion.Explanation);
        Assert.Contains("heuristic", suggestion.Explanation);

        long eventCount = fixture.Interactions.Read().TotalRecorded;
        int auditCount = (await fixture.CommandStore.GetSnapshotAsync(
            fixture.Target.Id)).AuditTrail.Count;

        // Passive recommendation reads cannot issue a command or train any
        // extra episode; the numeric score is an ordering heuristic only.
        for (int read = 0; read < 5; read++)
        {
            Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        }
        Assert.Equal(eventCount, fixture.Interactions.Read().TotalRecorded);
        Assert.Equal(auditCount,
            (await fixture.CommandStore.GetSnapshotAsync(fixture.Target.Id)).AuditTrail.Count);
        Assert.Equal(RelayState.Off,
            (await fixture.CommandStore.GetSnapshotAsync(fixture.Target.Id)).CurrentState);
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);

        // Resolving the source condition removes actionable advice but does
        // not invent, erase or retroactively relabel historical evidence.
        Assert.True(fixture.Issues.Resolve(fixture.Source.Id,
            DashboardIssueType.TelemetrySpike, fixture.Clock.UtcNow));
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
    }

    /// <summary>
    /// Uses the same concrete components as the production DI registration.
    /// An injected UTC clock makes ordering and expiry assertions deterministic.
    /// </summary>
    private sealed class Fixture
    {
        public FixedTimeProvider Clock { get; } = new(Anchor);
        public InMemorySensorRepository Sensors { get; } = new();
        public InMemoryActuatorCommandStore CommandStore { get; } = new();
        public DashboardIssueCollection Issues { get; } = new();
        public InMemoryOperatorInteractionStore Interactions { get; }
        public InMemoryInteractionPatternStore Patterns { get; }
        public PredictivePatternLearningService Learner { get; }
        public PredictiveActionService Predictor { get; }
        public ActuatorCommandService Commands { get; }
        public SensorProfile Source { get; }
        public SensorProfile Target { get; }

        private Fixture()
        {
            var policy = new PatternLearningPolicy();
            Interactions = new InMemoryOperatorInteractionStore(Clock);
            Patterns = new InMemoryInteractionPatternStore(Clock, policy);
            var dashboard = new DashboardIssueService(Issues, Clock);
            Learner = new PredictivePatternLearningService(
                Interactions, dashboard, Patterns, policy, Clock);
            Commands = new ActuatorCommandService(Sensors, CommandStore, Clock,
                interactions: Interactions, patternLearner: Learner);
            Predictor = new PredictiveActionService(Patterns, dashboard,
                Sensors, CommandStore, policy, Clock);
            Source = NewSensor("Source telemetry", SensorCategory.Environmental,
                TelemetryDataType.Float);
            Target = NewSensor("Commanded relay", SensorCategory.Actuator,
                TelemetryDataType.Boolean);
            Target.ChangeStatus(SensorStatus.Online, Clock.UtcNow);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Assert.True(await fixture.Sensors.TryAddAsync(fixture.Source));
            Assert.True(await fixture.Sensors.TryAddAsync(fixture.Target));
            return fixture;
        }

        public async Task<SensorProfile> RegisterActuatorAsync(string name)
        {
            SensorProfile profile = NewSensor(name, SensorCategory.Actuator,
                TelemetryDataType.Boolean);
            profile.ChangeStatus(SensorStatus.Online, Clock.UtcNow);
            Assert.True(await Sensors.TryAddAsync(profile));
            return profile;
        }

        public Guid OpenFreshSpikeEpisode()
        {
            // The true issue collection creates a new GUID only after
            // recovery; repeat observations in one episode do not add support.
            Issues.Resolve(Source.Id, DashboardIssueType.TelemetrySpike, Clock.UtcNow);
            DashboardIssue issue = Issues.AddOrUpdate(Source.Id, Source.Name,
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Critical, DashboardIssueTarget.Telemetry,
                "Active telemetry spike", "An 80% relative decrease in 2 minutes.",
                Clock.UtcNow);
            return issue.Id;
        }

        public void ViewAlert(Guid episodeId) =>
            Interactions.Append(OperatorInteractionKind.AlertViewed,
                deviceId: Source.Id, alertEpisodeId: episodeId);

        public void SelectActuator(Guid episodeId) =>
            Interactions.Append(OperatorInteractionKind.SearchSubmitted,
                deviceId: Target.Id, alertEpisodeId: episodeId);

        private SensorProfile NewSensor(string name, SensorCategory category,
            TelemetryDataType dataType) =>
            SensorProfile.Create(name,
                DeviceIdentifier.FromUniqueIdentifier($"p310-{Guid.NewGuid():N}"),
                new DeploymentLocation("Regression Lab", "Prediction", "Bench-1"),
                category, dataType, Clock.UtcNow);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}