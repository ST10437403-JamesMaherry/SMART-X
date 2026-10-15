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
/// Tests the genuine learning-to-ranking path: real active-episode records,
/// explicit operator events, state-changing commands, the P2-04 Undo Stack,
/// the bounded Dictionary projection and current sensor eligibility.
/// No test sets a precomputed pattern count or fakes an API success receipt.
/// </summary>
public sealed class PredictiveActionServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoHistory_ReturnsEmptySnapshotWithoutChangingCommandState()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Activate();

        PredictedActionSnapshot result = await fixture.Predictor.GetSnapshotAsync();

        Assert.Empty(result.Actions);
        Assert.Equal(3, result.MinimumSupportingEpisodes);
        Assert.Equal(TimeSpan.FromDays(7), result.EvidenceWindow);
        Assert.Equal(BaseTime, result.EvaluatedAtUtc);
        Assert.Equal(RelayState.Off, (await fixture.Store.GetSnapshotAsync(fixture.Target.Id)).CurrentState);
    }

    [Fact]
    public async Task TwoIndependentEpisodes_AreInsufficientButThirdProducesExplainedAction()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainAsync();
        await fixture.TrainAsync();

        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        await fixture.TrainAsync();
        PredictedAction action = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);

        Assert.Equal(3, action.SupportingEpisodes);
        Assert.Equal(fixture.Source.Id, action.SourceDeviceId);
        Assert.Equal(fixture.Target.Id, action.TargetActuatorId);
        Assert.Equal(RelayCommandType.RelayOn, action.Command);
        Assert.Equal(TelemetryMovementDirection.Decrease, action.Movement);
        Assert.Equal(DashboardIssueSeverity.Critical, action.Severity);
        Assert.Equal(85, action.Score);
        Assert.Equal(PredictionConfidenceBand.High, action.ConfidenceBand);
        Assert.Contains("3 distinct alert episodes", action.Explanation);
        Assert.Contains("relative decrease", action.Explanation);
        Assert.Contains("heuristic", action.Explanation);
        Assert.Equal(RelayState.Off, (await fixture.Store.GetSnapshotAsync(fixture.Target.Id)).CurrentState);
    }

    [Fact]
    public async Task RepeatedClicksAndUndo_DoNotInflateSupportingEpisodeCount()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainAsync();
        await fixture.TrainAsync();
        await fixture.TrainAsync();

        PatternStatistics learned = Assert.Single(fixture.Patterns.GetSnapshot());
        Assert.Equal(3, learned.SupportingEpisodes);
        Assert.Equal(3, learned.Episodes.Select(item => item.AlertEpisodeId).Distinct().Count());
        Assert.Equal(3, Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions).SupportingEpisodes);
    }

    [Fact]
    public async Task ReadingRecommendationsNeverAddsInteractionOrCommandEvents()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();
        long recordedBefore = fixture.Interactions.Read().TotalRecorded;
        int auditBefore = (await fixture.Store.GetSnapshotAsync(fixture.Target.Id)).AuditTrail.Count;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        }

        Assert.Equal(recordedBefore, fixture.Interactions.Read().TotalRecorded);
        Assert.Equal(auditBefore,
            (await fixture.Store.GetSnapshotAsync(fixture.Target.Id)).AuditTrail.Count);
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
    }

    [Fact]
    public async Task ResolvedEpisodeSuppressesSuggestion_AndNewMatchingEpisodeRestoresIt()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);

        Assert.True(fixture.Issues.Resolve(fixture.Source.Id,
            DashboardIssueType.TelemetrySpike, fixture.Clock.UtcNow));
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        Guid fresh = fixture.Activate();
        PredictedAction action = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(fresh, action.SourceAlertEpisodeId);
        Assert.Equal(3, action.SupportingEpisodes); // No new search/command was needed.
    }

    [Fact]
    public async Task NumericIncreaseCannotMatchTrainingFromRelativeDecrease()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();

        fixture.Activate(movement: TelemetryMovementDirection.Increase);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        fixture.Activate(movement: TelemetryMovementDirection.Decrease);
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task AmbiguousMovementAndSeverityMismatchCannotMatchTraining()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();

        fixture.Activate(severity: DashboardIssueSeverity.Warning);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        fixture.Activate(detailOverride: "Telemetry changed unexpectedly without a direction.");
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        fixture.Activate(detailOverride: "relative increase and relative decrease both reported");
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task OfflineMaintenanceAndDecommissionedTargetsAreIneligible()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();

        fixture.Target.ChangeStatus(SensorStatus.Offline, fixture.Clock.UtcNow);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        fixture.Target.ChangeStatus(SensorStatus.Maintenance, fixture.Clock.UtcNow);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        fixture.Target.ChangeStatus(SensorStatus.Online, fixture.Clock.UtcNow);
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        fixture.Target.ChangeStatus(SensorStatus.Decommissioned, fixture.Clock.UtcNow);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task LiveCategoryAndTelemetryTypeMustRemainActuatorBoolean()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();

        fixture.Target.UpdateDetails(fixture.Target.Name,
            fixture.Target.DeploymentLocation, SensorCategory.PowerConsumption,
            TelemetryDataType.Boolean, fixture.Clock.UtcNow);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        fixture.Target.UpdateDetails(fixture.Target.Name,
            fixture.Target.DeploymentLocation, SensorCategory.Actuator,
            TelemetryDataType.Float, fixture.Clock.UtcNow);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        fixture.Target.UpdateDetails(fixture.Target.Name,
            fixture.Target.DeploymentLocation, SensorCategory.Actuator,
            TelemetryDataType.Boolean, fixture.Clock.UtcNow);
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task CommandedStateSuppressesNoOpAndUndoRestoresEligibility()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);

        // No viewed alert/search association accompanies this extra command.
        ActuatorCommandExecutionResult on = await fixture.Commands.ExecuteAsync(
            fixture.Target.Id, RelayCommandType.RelayOn);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);

        await fixture.Commands.UndoAsync(fixture.Target.Id, on.Command.Id);
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
    }

    [Fact]
    public async Task ConditionOlderThanPolicyFreshnessIsSuppressed_WithoutErasingEvidence()
    {
        var policy = new PatternLearningPolicy(
            maximumCurrentConditionAge: TimeSpan.FromHours(2));
        Fixture fixture = await Fixture.CreateAsync(policy);
        await fixture.TrainThreeAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);

        // Another observation of the SAME live episode makes it fresh again.
        fixture.RefreshActive();
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task EvidenceExpiresAfterSevenDays_EvenIfCurrentAlertIsFresh()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(8));

        fixture.Activate();
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Empty(fixture.Patterns.GetSnapshot());
    }

    [Fact]
    public async Task FourEpisodesOutrankThree_WhenRecencyAndSeverityMatch()
    {
        Fixture fixture = await Fixture.CreateAsync();
        SensorProfile second = await fixture.AddActuatorAsync("Secondary Pump");
        await fixture.TrainThreeAsync(target: second);
        for (int i = 0; i < 4; i++)
        {
            await fixture.TrainAsync(target: fixture.Target);
        }

        PredictedAction[] ranked = (await fixture.Predictor.GetSnapshotAsync()).Actions.ToArray();
        Assert.Equal(2, ranked.Length);
        Assert.Equal(fixture.Target.Id, ranked[0].TargetActuatorId);
        Assert.Equal(4, ranked[0].SupportingEpisodes);
        Assert.Equal(100, ranked[0].Score);
        Assert.Equal(3, ranked[1].SupportingEpisodes);
        Assert.Equal(85, ranked[1].Score);
    }

    [Fact]
    public async Task MoreRecentThreeEpisodePatternOutranksOlderOne()
    {
        Fixture fixture = await Fixture.CreateAsync();
        SensorProfile newerTarget = await fixture.AddActuatorAsync("Later Pump");
        await fixture.TrainThreeAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await fixture.TrainThreeAsync(target: newerTarget);

        PredictedAction[] ranked = (await fixture.Predictor.GetSnapshotAsync()).Actions.ToArray();
        Assert.Equal(2, ranked.Length);
        Assert.Equal(newerTarget.Id, ranked[0].TargetActuatorId);
        Assert.Equal(85, ranked[0].Score);
        Assert.Equal(80, ranked[1].Score);
    }

    [Fact]
    public async Task EqualScoreSupportAndRecency_UseStableTargetIdentifierTieBreak()
    {
        Fixture fixture = await Fixture.CreateAsync();
        SensorProfile second = await fixture.AddActuatorAsync("Tie Pump");

        for (int i = 0; i < 3; i++)
        {
            // Both verified commands happen at the same controlled instant;
            // they have different genuine source-alert episode identities.
            await fixture.TrainAsync(target: fixture.Target, advanceClock: false);
            await fixture.TrainAsync(target: second, advanceClock: false);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        PredictedAction[] ranked = (await fixture.Predictor.GetSnapshotAsync()).Actions.ToArray();
        Assert.Equal(2, ranked.Length);
        Assert.Equal(ranked[0].Score, ranked[1].Score);
        Assert.Equal(ranked[0].LastLearnedAtUtc, ranked[1].LastLearnedAtUtc);
        Assert.Equal(new[] { fixture.Target.Id, second.Id }.OrderBy(id => id).ToArray(),
            ranked.Select(action => action.TargetActuatorId).ToArray());
        Assert.Equal(ranked.Select(action => action.TargetActuatorId),
            (await fixture.Predictor.GetSnapshotAsync()).Actions.Select(action => action.TargetActuatorId));
    }

    [Fact]
    public async Task CurrentSeverityContributesToRankingAcrossIndependentSources()
    {
        Fixture fixture = await Fixture.CreateAsync();
        SensorProfile warningSource = await fixture.AddSourceAsync("Secondary sensor");
        await fixture.TrainThreeAsync(source: warningSource,
            severity: DashboardIssueSeverity.Warning);
        await fixture.TrainThreeAsync();

        PredictedAction[] ranked = (await fixture.Predictor.GetSnapshotAsync()).Actions.ToArray();
        Assert.Equal(2, ranked.Length);
        Assert.Equal(fixture.Source.Id, ranked[0].SourceDeviceId);
        Assert.Equal(DashboardIssueSeverity.Critical, ranked[0].Severity);
        Assert.Equal(85, ranked[0].Score);
        Assert.Equal(DashboardIssueSeverity.Warning, ranked[1].Severity);
        Assert.Equal(80, ranked[1].Score);
    }

    [Fact]
    public async Task NonNumericDisconnectionPatternMatchesWithoutMovementDirection()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync(type: DashboardIssueType.SensorDisconnected);

        PredictedAction action = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(DashboardIssueType.SensorDisconnected, action.ConditionType);
        Assert.Equal(TelemetryMovementDirection.NotApplicable, action.Movement);
        Assert.Contains("sensor disconnection", action.Explanation);
    }

    [Fact]
    public async Task LearnedRelayOffIsSupportedButOnlyWhileTheRelayIsOn()
    {
        Fixture fixture = await Fixture.CreateAsync();

        // Prepare the actual relay for a state-changing RelayOff. No alert
        // view or association is present, so this preparatory RelayOn is not
        // a learned remedy.
        await fixture.Commands.ExecuteAsync(fixture.Target.Id, RelayCommandType.RelayOn);
        await fixture.TrainThreeAsync(command: RelayCommandType.RelayOff);
        Assert.Equal(RelayState.On, (await fixture.Store.GetSnapshotAsync(fixture.Target.Id)).CurrentState);

        PredictedAction action = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(RelayCommandType.RelayOff, action.Command);
        Assert.Equal(3, action.SupportingEpisodes);

        await fixture.Commands.ExecuteAsync(fixture.Target.Id, RelayCommandType.RelayOff);
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task OldEvidenceUsesEmergingBand_NotACalibratedProbability()
    {
        var policy = new PatternLearningPolicy(
            maximumCurrentConditionAge: TimeSpan.FromDays(7));
        Fixture fixture = await Fixture.CreateAsync(policy);
        await fixture.TrainThreeAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(5));
        fixture.Activate();

        PredictedAction action = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(64, action.Score); // 45 support + 4 recency + 15 severity
        Assert.Equal(PredictionConfidenceBand.Emerging, action.ConfidenceBand);
        Assert.Contains("not a probability", action.Explanation);
    }

    [Fact]
    public async Task ResultLimitAndExpiryTimestampAreBoundedAndExplicit()
    {
        var policy = new PatternLearningPolicy(maximumRecommendations: 1,
            maximumCurrentConditionAge: TimeSpan.FromDays(30));
        Fixture fixture = await Fixture.CreateAsync(policy);
        SensorProfile another = await fixture.AddActuatorAsync("Second Pump");
        await fixture.TrainThreeAsync(target: another);
        await fixture.TrainThreeAsync(target: fixture.Target);

        PredictedAction suggestion = Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
        Assert.Equal(fixture.Target.Id, suggestion.TargetActuatorId);
        PatternStatistics source = fixture.Patterns.GetSnapshot()
            .Single(pattern => pattern.Key.TargetActuatorId == fixture.Target.Id);
        Assert.Equal(source.Episodes[0].ObservedAtUtc + TimeSpan.FromDays(7),
            suggestion.ExpiresAtUtc);
        Assert.InRange(suggestion.Score, 0, 100);
    }

    [Fact]
    public async Task ConfigurableMinimumFourEpisodesIsEnforced()
    {
        Fixture fixture = await Fixture.CreateAsync(
            new PatternLearningPolicy(minimumSupportingEpisodes: 4));
        await fixture.TrainThreeAsync();
        Assert.Empty((await fixture.Predictor.GetSnapshotAsync()).Actions);
        await fixture.TrainAsync();
        Assert.Single((await fixture.Predictor.GetSnapshotAsync()).Actions);
    }

    [Fact]
    public async Task CancelledRankingDoesNotMutatePatternStore()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.TrainThreeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Predictor.GetSnapshotAsync(cancellation.Token));
        Assert.Equal(3, Assert.Single(fixture.Patterns.GetSnapshot()).SupportingEpisodes);
    }

    [Fact]
    public void PolicyRejectsImpossibleOrUnsafeRecommendationParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PatternLearningPolicy(minimumSupportingEpisodes: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PatternLearningPolicy(maximumCurrentConditionAge: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PatternLearningPolicy(maximumRecommendations: 101));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PatternLearningPolicy(maximumRecommendations: 0));
    }

    private sealed class Fixture
    {
        public MutableTimeProvider Clock { get; } = new(BaseTime);
        public InMemorySensorRepository Sensors { get; } = new();
        public InMemoryActuatorCommandStore Store { get; } = new();
        public DashboardIssueCollection Issues { get; } = new();
        public InMemoryOperatorInteractionStore Interactions { get; }
        public InMemoryInteractionPatternStore Patterns { get; }
        public ActuatorCommandService Commands { get; }
        public PredictiveActionService Predictor { get; }
        public SensorProfile Source { get; }
        public SensorProfile Target { get; }

        private Fixture(PatternLearningPolicy? suppliedPolicy)
        {
            PatternLearningPolicy policy = suppliedPolicy ?? new PatternLearningPolicy();
            Interactions = new InMemoryOperatorInteractionStore(Clock);
            Patterns = new InMemoryInteractionPatternStore(Clock, policy);
            DashboardIssueService dashboard = new(Issues, Clock);
            var learner = new PredictivePatternLearningService(
                Interactions, dashboard, Patterns, policy, Clock);

            Commands = new ActuatorCommandService(Sensors, Store, Clock,
                interactions: Interactions, patternLearner: learner);
            Predictor = new PredictiveActionService(Patterns, dashboard,
                Sensors, Store, policy, Clock);

            Source = NewProfile("Moisture source", SensorCategory.Environmental,
                TelemetryDataType.Float);
            Target = NewProfile("Pump relay", SensorCategory.Actuator,
                TelemetryDataType.Boolean);
            Target.ChangeStatus(SensorStatus.Online, Clock.UtcNow);
        }

        public static async Task<Fixture> CreateAsync(PatternLearningPolicy? policy = null)
        {
            var fixture = new Fixture(policy);
            Assert.True(await fixture.Sensors.TryAddAsync(fixture.Source));
            Assert.True(await fixture.Sensors.TryAddAsync(fixture.Target));
            return fixture;
        }

        public async Task<SensorProfile> AddActuatorAsync(string name)
        {
            SensorProfile target = NewProfile(name, SensorCategory.Actuator,
                TelemetryDataType.Boolean);
            target.ChangeStatus(SensorStatus.Online, Clock.UtcNow);
            Assert.True(await Sensors.TryAddAsync(target));
            return target;
        }

        public Guid Activate(
            DashboardIssueSeverity severity = DashboardIssueSeverity.Critical,
            TelemetryMovementDirection movement = TelemetryMovementDirection.Decrease,
            string? detailOverride = null,
            SensorProfile? source = null,
            DashboardIssueType type = DashboardIssueType.TelemetrySpike)
        {
            // Resolve any previous episode before activating a new one. The
            // real collection's HashSet creates a fresh identity on recurrence.
            SensorProfile origin = source ?? Source;
            Issues.Resolve(origin.Id, type, Clock.UtcNow);
            string direction = movement == TelemetryMovementDirection.Decrease
                ? "relative decrease" : "relative increase";
            string detail = type == DashboardIssueType.TelemetrySpike
                ? $"Telemetry changed 80%, a {direction}, over 2 minutes."
                : "Sensor connection lost during the controlled scenario.";
            DashboardIssue issue = Issues.AddOrUpdate(origin.Id, origin.Name,
                type, severity,
                DashboardIssueTarget.Telemetry, "Active operational issue",
                detailOverride ?? detail,
                Clock.UtcNow);
            return issue.Id;
        }

        public async Task TrainAsync(SensorProfile? target = null,
            bool advanceClock = true,
            SensorProfile? source = null,
            DashboardIssueSeverity severity = DashboardIssueSeverity.Critical,
            DashboardIssueType type = DashboardIssueType.TelemetrySpike,
            RelayCommandType command = RelayCommandType.RelayOn)
        {
            SensorProfile chosen = target ?? Target;
            SensorProfile origin = source ?? Source;
            Guid episode = Activate(source: origin, severity: severity, type: type);
            Interactions.Append(OperatorInteractionKind.AlertViewed,
                deviceId: origin.Id, alertEpisodeId: episode);
            Interactions.Append(OperatorInteractionKind.SearchSubmitted,
                deviceId: chosen.Id, alertEpisodeId: episode);

            // These calls mutate the real P2-04 command Stack, append a real
            // CommandExecuted event, and invoke the P2-09 learning service.
            ActuatorCommandExecutionResult result = await Commands.ExecuteAsync(
                chosen.Id, command);
            Assert.True(result.StateChanged);

            // A genuine Undo resets commanded state so the next independent
            // episode can again produce a state-changing RelayOn remedy.
            ActuatorCommandExecutionResult undo = await Commands.UndoAsync(
                chosen.Id, result.Command.Id);
            Assert.True(undo.IsReversal);
            if (advanceClock)
            {
                Clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        public async Task TrainThreeAsync(SensorProfile? target = null,
            SensorProfile? source = null,
            DashboardIssueSeverity severity = DashboardIssueSeverity.Critical,
            DashboardIssueType type = DashboardIssueType.TelemetrySpike,
            RelayCommandType command = RelayCommandType.RelayOn)
        {
            for (int i = 0; i < 3; i++)
            {
                await TrainAsync(target, source: source, severity: severity,
                    type: type, command: command);
            }
        }

        public async Task<SensorProfile> AddSourceAsync(string name)
        {
            SensorProfile source = NewProfile(name, SensorCategory.Environmental,
                TelemetryDataType.Float);
            Assert.True(await Sensors.TryAddAsync(source));
            return source;
        }

        public void RefreshActive()
        {
            Issues.AddOrUpdate(Source.Id, Source.Name, DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Critical, DashboardIssueTarget.Telemetry,
                "Critical telemetry spike", "Telemetry changed 80%, a relative decrease, over 2 minutes.",
                Clock.UtcNow);
        }

        private SensorProfile NewProfile(string name,
            SensorCategory category, TelemetryDataType telemetryType) =>
            SensorProfile.Create(name,
                DeviceIdentifier.FromUniqueIdentifier($"prediction-{Guid.NewGuid():N}"),
                new DeploymentLocation("Lab", "Prediction", "Node-A"),
                category, telemetryType, Clock.UtcNow);
    }

    private sealed class MutableTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = instant;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}