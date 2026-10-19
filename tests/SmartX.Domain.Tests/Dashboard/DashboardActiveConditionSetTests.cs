using System.Collections.Concurrent;

using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Tests.Dashboard;

/// <summary>
/// Tests the real HashSet-backed active-condition lifecycle without creating
/// a second issue store or changing existing Adaptive Anomaly Spotlight rules.
///
/// Each active condition is identified by its registered sensor GUID
/// and issue type.
/// </summary>
public sealed class DashboardActiveConditionSetTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewCondition_AddsOneMembershipOneIssueAndOneActiveRow()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        Assert.Equal(0, collection.ActiveConditionCount);

        Assert.False(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        DashboardIssue issue = Report(collection, sensorId);

        Assert.Equal(1, collection.ActiveConditionCount);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.False(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.SensorDisconnected));

        Assert.Same(
            issue,
            Assert.Single(collection.GetActiveSnapshot(BaseTime)));

        Assert.Same(issue, Assert.Single(collection));

        Assert.Equal(1, issue.OccurrenceCount);
        Assert.Equal(DashboardIssueState.Active, issue.State);
    }

    [Fact]
    public void RepeatedCondition_ReusesOriginalActivationAndEscalatesSeverity()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        DashboardIssue first = Report(
            collection,
            sensorId,
            severity: DashboardIssueSeverity.Warning);

        DashboardIssue second = Report(
            collection,
            sensorId,
            severity: DashboardIssueSeverity.Critical,
            atUtc: BaseTime.AddMinutes(1));

        DashboardIssue third = Report(
            collection,
            sensorId,
            severity: DashboardIssueSeverity.Warning,
            atUtc: BaseTime.AddMinutes(2));

        Assert.Same(first, second);
        Assert.Same(first, third);

        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);

        Assert.Single(
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(3)));

        Assert.Equal(3, first.OccurrenceCount);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            first.Severity);

        Assert.Equal(
            BaseTime,
            first.FirstObservedAtUtc);

        Assert.Equal(
            BaseTime.AddMinutes(2),
            first.LastObservedAtUtc);
    }

    [Fact]
    public void ConditionKey_SeparatesTypesOnOneSensorAndSameTypeAcrossSensors()
    {
        var collection = new DashboardIssueCollection();

        Guid firstSensor = Guid.NewGuid();
        Guid secondSensor = Guid.NewGuid();

        DashboardIssue spikeA =
            Report(collection, firstSensor);

        DashboardIssue disconnectedA =
            Report(
                collection,
                firstSensor,
                type: DashboardIssueType.SensorDisconnected);

        DashboardIssue spikeB =
            Report(collection, secondSensor);

        DashboardIssue configurationB =
            Report(
                collection,
                secondSensor,
                type: DashboardIssueType.ConfigurationIssue);

        Assert.Equal(4, collection.ActiveConditionCount);
        Assert.Equal(4, collection.Count);

        Assert.Equal(
            4,
            collection.GetActiveSnapshot(BaseTime).Count);

        Assert.Equal(
            4,
            new[]
            {
                spikeA.Id,
                disconnectedA.Id,
                spikeB.Id,
                configurationB.Id
            }.Distinct().Count());

        Assert.True(
            collection.Resolve(
                firstSensor,
                DashboardIssueType.TelemetrySpike,
                BaseTime.AddMinutes(1)));

        Assert.Equal(3, collection.ActiveConditionCount);

        Assert.False(
            collection.ContainsActiveCondition(
                firstSensor,
                DashboardIssueType.TelemetrySpike));

        Assert.True(
            collection.ContainsActiveCondition(
                firstSensor,
                DashboardIssueType.SensorDisconnected));

        Assert.True(
            collection.ContainsActiveCondition(
                secondSensor,
                DashboardIssueType.TelemetrySpike));

        Assert.True(
            collection.ContainsActiveCondition(
                secondSensor,
                DashboardIssueType.ConfigurationIssue));

        Assert.Equal(
            3,
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(2)).Count);

        Assert.Equal(
            DashboardIssueState.Resolved,
            spikeA.State);
    }

    [Fact]
    public void Acknowledgement_KeepsMembershipAndRepeatedFailureCounters()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        DashboardIssue issue =
            Report(collection, sensorId);

        Assert.True(
            collection.Acknowledge(
                issue.Id,
                BaseTime.AddMinutes(1)));

        Assert.Equal(
            DashboardIssueState.Acknowledged,
            issue.State);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Equal(1, collection.ActiveConditionCount);

        DashboardIssue repeated = Report(
            collection,
            sensorId,
            severity: DashboardIssueSeverity.Critical,
            atUtc: BaseTime.AddMinutes(2));

        Assert.Same(issue, repeated);

        Assert.Equal(
            DashboardIssueState.Acknowledged,
            repeated.State);

        Assert.Equal(2, repeated.OccurrenceCount);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            repeated.Severity);

        Assert.Single(
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(3)));

        Assert.Equal(1, collection.ActiveConditionCount);
    }

    [Fact]
    public void Recovery_RemovesMembershipAndLaterRecurrenceCreatesANewEpisode()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        DashboardIssue original =
            Report(collection, sensorId);

        Assert.True(
            collection.Resolve(
                sensorId,
                DashboardIssueType.TelemetrySpike,
                BaseTime.AddMinutes(1)));

        Assert.False(
            collection.Resolve(
                sensorId,
                DashboardIssueType.TelemetrySpike,
                BaseTime.AddMinutes(2)));

        Assert.Equal(0, collection.ActiveConditionCount);

        Assert.False(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Empty(
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(2)));

        Assert.Equal(
            DashboardIssueState.Resolved,
            original.State);

        Assert.Equal(
            BaseTime.AddMinutes(1),
            original.ResolvedAtUtc);

        DashboardIssue recurrence = Report(
            collection,
            sensorId,
            atUtc: BaseTime.AddMinutes(3));

        Assert.NotEqual(original.Id, recurrence.Id);
        Assert.Equal(2, collection.Count);
        Assert.Equal(1, collection.ActiveConditionCount);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Equal(
            BaseTime.AddMinutes(3),
            recurrence.FirstObservedAtUtc);

        Assert.Equal(1, recurrence.OccurrenceCount);

        Assert.Same(
            recurrence,
            Assert.Single(
                collection.GetActiveSnapshot(
                    BaseTime.AddMinutes(4))));

        Assert.Contains(
            collection.GetPrioritizedSnapshot(
                BaseTime.AddMinutes(4)),
            issue =>
                issue.Id == original.Id &&
                issue.State == DashboardIssueState.Resolved);
    }

    [Fact]
    public void ConcurrentDuplicateObservations_ProduceOneActivationAndCountEveryOccurrence()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        var returnedIds = new ConcurrentBag<Guid>();

        // Identical timestamps avoid introducing unrelated out-of-order
        // telemetry validation into this concurrency test.
        Parallel.For(0, 256, index =>
        {
            DashboardIssue issue = Report(
                collection,
                sensorId,
                severity: index % 8 == 0
                    ? DashboardIssueSeverity.Critical
                    : DashboardIssueSeverity.Warning);

            returnedIds.Add(issue.Id);
        });

        Assert.Equal(256, returnedIds.Count);

        // Every concurrent report returned the same activation episode.
        Assert.Single(returnedIds.Distinct());

        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);

        DashboardIssue active =
            Assert.Single(
                collection.GetActiveSnapshot(BaseTime));

        Assert.Equal(256, active.OccurrenceCount);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            active.Severity);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Single(
            collection.GetPrioritizedSnapshot(BaseTime));
    }

    [Fact]
    public void InvalidNewReport_DoesNotReserveMembershipOrWriteIssueHistory()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            collection.AddOrUpdate(
                sensorId,
                "Sensor",
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Telemetry,
                "",
                "Detailed evidence",
                BaseTime));

        Assert.Throws<ArgumentException>(() =>
            collection.AddOrUpdate(
                sensorId,
                "Sensor",
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Telemetry,
                "Valid title",
                "Detailed evidence",
                default(DateTimeOffset)));

        Assert.Equal(0, collection.ActiveConditionCount);
        Assert.Empty(collection);

        Assert.False(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Empty(
            collection.GetActiveSnapshot(BaseTime));

        // After invalid reports, a valid activation starts at occurrence 1.
        DashboardIssue first =
            Report(collection, sensorId);

        Assert.Equal(1, first.OccurrenceCount);
        Assert.Equal(1, collection.ActiveConditionCount);
    }

    [Fact]
    public void FailedRepeatAndFailedRecovery_KeepTheOriginalMembershipAndIssue()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        DashboardIssue original = Report(
            collection,
            sensorId,
            atUtc: BaseTime.AddMinutes(2));

        // Earlier repeated observations and invalid recovery timestamps
        // must not remove membership or replace the original episode.
        Assert.Throws<ArgumentException>(() =>
            Report(
                collection,
                sensorId,
                atUtc: BaseTime.AddMinutes(1)));

        Assert.Throws<ArgumentException>(() =>
            collection.Resolve(
                sensorId,
                DashboardIssueType.TelemetrySpike,
                BaseTime));

        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorId,
                DashboardIssueType.TelemetrySpike));

        Assert.Same(
            original,
            Assert.Single(
                collection.GetActiveSnapshot(
                    BaseTime.AddMinutes(3))));

        Assert.Equal(1, original.OccurrenceCount);
        Assert.Equal(DashboardIssueState.Active, original.State);
        Assert.Null(original.ResolvedAtUtc);
    }

    [Fact]
    public void ResolvedHistoryTrimming_NeverRemovesUnresolvedMembership()
    {
        var collection =
            new DashboardIssueCollection(maximumHistory: 2);

        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        DashboardIssue old =
            Report(collection, first);

        Report(collection, second);

        Assert.True(
            collection.Resolve(
                first,
                DashboardIssueType.TelemetrySpike,
                BaseTime.AddMinutes(1)));

        DashboardIssue newest = Report(
            collection,
            third,
            atUtc: BaseTime.AddMinutes(2));

        // The history cap removes the resolved episode, not active ones.
        Assert.Equal(2, collection.Count);
        Assert.Equal(2, collection.ActiveConditionCount);

        Assert.DoesNotContain(
            collection,
            issue => issue.Id == old.Id);

        Assert.True(
            collection.ContainsActiveCondition(
                second,
                DashboardIssueType.TelemetrySpike));

        Assert.True(
            collection.ContainsActiveCondition(
                third,
                DashboardIssueType.TelemetrySpike));

        Assert.Equal(
            newest.Id,
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(3))
                .Single(issue => issue.SensorProfileId == third)
                .Id);

        // Recurrence is still possible after the old resolved row is pruned.
        DashboardIssue recurrence = Report(
            collection,
            first,
            atUtc: BaseTime.AddMinutes(3));

        Assert.NotEqual(old.Id, recurrence.Id);
        Assert.Equal(3, collection.ActiveConditionCount);
        Assert.Equal(3, collection.Count);
    }

    [Fact]
    public void InvalidMembershipKeys_ThrowWithoutAlteringTheSet()
    {
        var collection = new DashboardIssueCollection();
        Guid sensorId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            collection.ContainsActiveCondition(
                Guid.Empty,
                DashboardIssueType.TelemetrySpike));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            collection.ContainsActiveCondition(
                sensorId,
                (DashboardIssueType)999));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Report(
                collection,
                sensorId,
                type: (DashboardIssueType)999));

        Assert.Throws<ArgumentException>(() =>
            collection.Resolve(
                Guid.Empty,
                DashboardIssueType.TelemetrySpike,
                BaseTime));

        Assert.Equal(0, collection.ActiveConditionCount);
        Assert.Empty(collection);
    }

    /// <summary>
    /// Creates one validated issue observation while selecting the
    /// appropriate Part 1 investigation target for each issue type.
    /// </summary>
    private static DashboardIssue Report(
        DashboardIssueCollection collection,
        Guid sensorId,
        DashboardIssueType type = DashboardIssueType.TelemetrySpike,
        DashboardIssueSeverity severity = DashboardIssueSeverity.Warning,
        DateTimeOffset? atUtc = null)
    {
        DashboardIssueTarget target = type switch
        {
            DashboardIssueType.ConfigurationIssue =>
                DashboardIssueTarget.Attachments,

            DashboardIssueType.SensorDisconnected or
                DashboardIssueType.SensorStateChange =>
                DashboardIssueTarget.Sensor,

            _ => DashboardIssueTarget.Telemetry
        };

        return collection.AddOrUpdate(
            sensorId,
            "P2-06 Sensor",
            type,
            severity,
            target,
            "Active condition detected",
            "Real operational evidence for this active condition.",
            atUtc ?? BaseTime);
    }
}