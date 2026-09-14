using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Tests.Dashboard;

public sealed class DashboardIssueCollectionTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            12,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void AddOrUpdate_WhenConditionNew_AddsIssue()
    {
        DashboardIssueCollection collection = new();

        DashboardIssue issue =
            AddSpike(
                collection,
                Guid.NewGuid(),
                DashboardIssueSeverity.Warning,
                BaseTime);

        Assert.Single(collection);

        Assert.Equal(
            1,
            issue.OccurrenceCount);
    }

    [Fact]
    public void AddOrUpdate_WhenConditionRepeated_UpdatesExistingIssue()
    {
        DashboardIssueCollection collection = new();

        Guid sensorId = Guid.NewGuid();

        DashboardIssue first =
            AddSpike(
                collection,
                sensorId,
                DashboardIssueSeverity.Warning,
                BaseTime);

        DashboardIssue repeated =
            AddSpike(
                collection,
                sensorId,
                DashboardIssueSeverity.Critical,
                BaseTime.AddMinutes(1));

        Assert.Single(collection);

        Assert.Same(
            first,
            repeated);

        Assert.Equal(
            2,
            repeated.OccurrenceCount);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            repeated.Severity);
    }

    [Fact]
    public void AddOrUpdate_WhenDifferentIssueTypes_AddsSeparateIssues()
    {
        DashboardIssueCollection collection = new();

        Guid sensorId = Guid.NewGuid();

        AddSpike(
            collection,
            sensorId,
            DashboardIssueSeverity.Warning,
            BaseTime);

        collection.AddOrUpdate(
            sensorId,
            "Temperature Sensor",
            DashboardIssueType.ConfigurationIssue,
            DashboardIssueSeverity.Information,
            DashboardIssueTarget.Attachments,
            "Configuration evidence missing",
            "No configuration attachment is available.",
            BaseTime);

        Assert.Equal(
            2,
            collection.Count);
    }

    [Fact]
    public void Resolve_WhenConditionExists_RemovesItFromActiveSnapshot()
    {
        DashboardIssueCollection collection = new();

        Guid sensorId = Guid.NewGuid();

        DashboardIssue issue =
            AddSpike(
                collection,
                sensorId,
                DashboardIssueSeverity.Warning,
                BaseTime);

        bool resolved =
            collection.Resolve(
                sensorId,
                DashboardIssueType.TelemetrySpike,
                BaseTime.AddMinutes(1));

        IReadOnlyList<DashboardIssue> activeIssues =
            collection.GetActiveSnapshot(
                BaseTime.AddMinutes(2));

        Assert.True(resolved);
        Assert.Empty(activeIssues);

        Assert.Equal(
            DashboardIssueState.Resolved,
            issue.State);
    }

    [Fact]
    public void AddOrUpdate_AfterResolution_CreatesNewIssueLifecycle()
    {
        DashboardIssueCollection collection = new();

        Guid sensorId = Guid.NewGuid();

        DashboardIssue original =
            AddSpike(
                collection,
                sensorId,
                DashboardIssueSeverity.Warning,
                BaseTime);

        collection.Resolve(
            sensorId,
            DashboardIssueType.TelemetrySpike,
            BaseTime.AddMinutes(1));

        DashboardIssue recurrence =
            AddSpike(
                collection,
                sensorId,
                DashboardIssueSeverity.Warning,
                BaseTime.AddMinutes(2));

        Assert.Equal(
            2,
            collection.Count);

        Assert.NotEqual(
            original.Id,
            recurrence.Id);

        Assert.Equal(
            DashboardIssueState.Resolved,
            original.State);

        Assert.Equal(
            DashboardIssueState.Active,
            recurrence.State);
    }

    [Fact]
    public void Acknowledge_WhenIssueExists_ChangesOperatorState()
    {
        DashboardIssueCollection collection = new();

        DashboardIssue issue =
            AddSpike(
                collection,
                Guid.NewGuid(),
                DashboardIssueSeverity.Warning,
                BaseTime);

        bool acknowledged =
            collection.Acknowledge(
                issue.Id,
                BaseTime.AddMinutes(1));

        Assert.True(acknowledged);

        Assert.Equal(
            DashboardIssueState.Acknowledged,
            issue.State);
    }

    [Fact]
    public void GetPrioritizedSnapshot_OrdersCriticalBeforeWarning()
    {
        DashboardIssueCollection collection = new();

        DashboardIssue warning =
            AddSpike(
                collection,
                Guid.NewGuid(),
                DashboardIssueSeverity.Warning,
                BaseTime);

        DashboardIssue critical =
            AddSpike(
                collection,
                Guid.NewGuid(),
                DashboardIssueSeverity.Critical,
                BaseTime);

        IReadOnlyList<DashboardIssue> issues =
            collection.GetPrioritizedSnapshot(
                BaseTime);

        Assert.Equal(
            critical.Id,
            issues[0].Id);

        Assert.Equal(
            warning.Id,
            issues[1].Id);
    }

    [Fact]
    public void GetPrioritizedSnapshot_RepeatedIssueCanRiseWithinSeverity()
    {
        DashboardIssueCollection collection = new();

        Guid repeatedSensorId =
            Guid.NewGuid();

        DashboardIssue first =
            AddSpike(
                collection,
                Guid.NewGuid(),
                DashboardIssueSeverity.Warning,
                BaseTime);

        DashboardIssue repeated =
            AddSpike(
                collection,
                repeatedSensorId,
                DashboardIssueSeverity.Warning,
                BaseTime);

        AddSpike(
            collection,
            repeatedSensorId,
            DashboardIssueSeverity.Warning,
            BaseTime);

        IReadOnlyList<DashboardIssue> issues =
            collection.GetPrioritizedSnapshot(
                BaseTime);

        Assert.Equal(
            repeated.Id,
            issues[0].Id);

        Assert.Equal(
            first.Id,
            issues[1].Id);
    }

    [Fact]
    public void GetEnumerator_ReturnsStableSnapshot()
    {
        DashboardIssueCollection collection = new();

        AddSpike(
            collection,
            Guid.NewGuid(),
            DashboardIssueSeverity.Warning,
            BaseTime);

        DashboardIssue[] snapshot =
            collection.ToArray();

        Assert.Single(snapshot);
    }

    [Fact]
    public void HistoryCapacity_DoesNotDiscardActiveIssues()
    {
        DashboardIssueCollection collection =
            new(maximumHistory: 1);

        AddSpike(
            collection,
            Guid.NewGuid(),
            DashboardIssueSeverity.Warning,
            BaseTime);

        AddSpike(
            collection,
            Guid.NewGuid(),
            DashboardIssueSeverity.Warning,
            BaseTime);

        /*
         * The retention limit applies to disposable resolved history, not
         * unresolved operational issues.
         */
        Assert.Equal(
            2,
            collection.Count);
    }

    [Fact]
    public void HistoryCapacity_RemovesOldestResolvedIssue()
    {
        DashboardIssueCollection collection =
            new(maximumHistory: 2);

        Guid firstSensorId =
            Guid.NewGuid();

        Guid secondSensorId =
            Guid.NewGuid();

        AddSpike(
            collection,
            firstSensorId,
            DashboardIssueSeverity.Warning,
            BaseTime);

        collection.Resolve(
            firstSensorId,
            DashboardIssueType.TelemetrySpike,
            BaseTime.AddMinutes(1));

        AddSpike(
            collection,
            secondSensorId,
            DashboardIssueSeverity.Warning,
            BaseTime.AddMinutes(2));

        AddSpike(
            collection,
            Guid.NewGuid(),
            DashboardIssueSeverity.Warning,
            BaseTime.AddMinutes(3));

        /*
         * The first resolved event is the only safely removable item. Both
         * current active conditions remain retained.
         */
        Assert.Equal(
            2,
            collection.Count);

        Assert.DoesNotContain(
            collection,
            issue =>
                issue.SensorProfileId ==
                firstSensorId);

        Assert.Contains(
            collection,
            issue =>
                issue.SensorProfileId ==
                secondSensorId);
    }

    private static DashboardIssue AddSpike(
        DashboardIssueCollection collection,
        Guid sensorId,
        DashboardIssueSeverity severity,
        DateTimeOffset observedAtUtc)
    {
        return collection.AddOrUpdate(
            sensorId,
            "Temperature Sensor",
            DashboardIssueType.TelemetrySpike,
            severity,
            DashboardIssueTarget.Telemetry,
            "Telemetry spike detected",
            "The latest reading changed rapidly.",
            observedAtUtc);
    }
}