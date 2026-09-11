using SmartX.Application.Dashboard;
using SmartX.Domain.Dashboard;

namespace SmartX.Application.Tests.Dashboard;

/// <summary>
/// Verifies the operator-facing application service that exposes and
/// acknowledges Adaptive Anomaly Spotlight issues.
///
/// Domain issue creation, prioritisation and deduplication have their own
/// dedicated tests. These tests focus on the Application-layer contract:
/// snapshot filtering, summary counts and acknowledgement behaviour.
/// </summary>
public sealed class DashboardIssueServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            14,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void GetSnapshot_ByDefault_ExcludesResolvedHistory()
    {
        var issues =
            new DashboardIssueCollection();

        DashboardIssue activeIssue =
            AddIssue(
                issues,
                Guid.NewGuid(),
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Critical,
                DashboardIssueTarget.Telemetry,
                BaseTime);

        Guid resolvedSensorId =
            Guid.NewGuid();

        DashboardIssue resolvedIssue =
            AddIssue(
                issues,
                resolvedSensorId,
                DashboardIssueType.ConfigurationIssue,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Attachments,
                BaseTime);

        bool resolved =
            issues.Resolve(
                resolvedSensorId,
                DashboardIssueType.ConfigurationIssue,
                BaseTime.AddMinutes(1));

        Assert.True(
            resolved);

        DashboardIssueService service =
            CreateService(
                issues,
                BaseTime.AddMinutes(2));

        var snapshot =
            service.GetSnapshot();

        var response =
            Assert.Single(
                snapshot.Issues);

        Assert.Equal(
            activeIssue.Id,
            response.Id);

        Assert.DoesNotContain(
            snapshot.Issues,
            issue =>
                issue.Id ==
                resolvedIssue.Id);

        Assert.Equal(
            1,
            snapshot.ActiveIssueCount);

        Assert.Equal(
            1,
            snapshot.UnacknowledgedIssueCount);
    }

    [Fact]
    public void GetSnapshot_WhenResolvedRequested_IncludesHistoricalIssue()
    {
        var issues =
            new DashboardIssueCollection();

        DashboardIssue activeIssue =
            AddIssue(
                issues,
                Guid.NewGuid(),
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Critical,
                DashboardIssueTarget.Telemetry,
                BaseTime);

        Guid resolvedSensorId =
            Guid.NewGuid();

        DashboardIssue resolvedIssue =
            AddIssue(
                issues,
                resolvedSensorId,
                DashboardIssueType.ConfigurationIssue,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Attachments,
                BaseTime);

        issues.Resolve(
            resolvedSensorId,
            DashboardIssueType.ConfigurationIssue,
            BaseTime.AddMinutes(1));

        DashboardIssueService service =
            CreateService(
                issues,
                BaseTime.AddMinutes(2));

        var snapshot =
            service.GetSnapshot(
                includeResolved: true);

        Assert.Equal(
            2,
            snapshot.Issues.Count);

        Assert.Contains(
            snapshot.Issues,
            issue =>
                issue.Id ==
                activeIssue.Id &&
                issue.State ==
                nameof(
                    DashboardIssueState.Active));

        Assert.Contains(
            snapshot.Issues,
            issue =>
                issue.Id ==
                resolvedIssue.Id &&
                issue.State ==
                nameof(
                    DashboardIssueState.Resolved));

        /*
         * Resolved history is returned for inspection but does not contribute
         * to the current active/unacknowledged summary counters.
         */
        Assert.Equal(
            1,
            snapshot.ActiveIssueCount);

        Assert.Equal(
            1,
            snapshot.UnacknowledgedIssueCount);
    }

    [Fact]
    public void Acknowledge_WhenIssueActive_PreservesIssueAndReducesPriority()
    {
        var issues =
            new DashboardIssueCollection();

        DashboardIssue issue =
            AddIssue(
                issues,
                Guid.NewGuid(),
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Critical,
                DashboardIssueTarget.Telemetry,
                BaseTime);

        DashboardIssueService service =
            CreateService(
                issues,
                BaseTime.AddMinutes(1));

        var before =
            service.GetSnapshot();

        double priorityBefore =
            Assert.Single(
                    before.Issues)
                .PriorityScore;

        var acknowledged =
            service.Acknowledge(
                issue.Id);

        Assert.Equal(
            nameof(
                DashboardIssueState.Acknowledged),
            acknowledged.State);

        Assert.NotNull(
            acknowledged.AcknowledgedAtUtc);

        Assert.True(
            acknowledged.PriorityScore <
            priorityBefore);

        var after =
            service.GetSnapshot();

        var remainingIssue =
            Assert.Single(
                after.Issues);

        Assert.Equal(
            issue.Id,
            remainingIssue.Id);

        Assert.Equal(
            nameof(
                DashboardIssueState.Acknowledged),
            remainingIssue.State);

        Assert.Equal(
            1,
            after.ActiveIssueCount);

        Assert.Equal(
            0,
            after.UnacknowledgedIssueCount);
    }

    [Fact]
    public void Acknowledge_WhenIssueDoesNotExist_ThrowsNotFoundException()
    {
        var service =
            CreateService(
                new DashboardIssueCollection(),
                BaseTime);

        Guid missingIssueId =
            Guid.NewGuid();

        var exception =
            Assert.Throws<
                DashboardIssueNotFoundException>(
                () =>
                    service.Acknowledge(
                        missingIssueId));

        Assert.Equal(
            missingIssueId,
            exception.IssueId);
    }

    [Fact]
    public void Acknowledge_WhenIssueResolved_ThrowsOperationException()
    {
        var issues =
            new DashboardIssueCollection();

        Guid sensorId =
            Guid.NewGuid();

        DashboardIssue issue =
            AddIssue(
                issues,
                sensorId,
                DashboardIssueType.ConfigurationIssue,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Attachments,
                BaseTime);

        issues.Resolve(
            sensorId,
            DashboardIssueType.ConfigurationIssue,
            BaseTime.AddMinutes(1));

        DashboardIssueService service =
            CreateService(
                issues,
                BaseTime.AddMinutes(2));

        var exception =
            Assert.Throws<
                DashboardIssueOperationException>(
                () =>
                    service.Acknowledge(
                        issue.Id));

        Assert.Contains(
            "resolved",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DashboardIssueService CreateService(
        DashboardIssueCollection issues,
        DateTimeOffset currentTime)
    {
        return new DashboardIssueService(
            issues,
            new FixedTimeProvider(
                currentTime));
    }

    private static DashboardIssue AddIssue(
        DashboardIssueCollection issues,
        Guid sensorId,
        DashboardIssueType type,
        DashboardIssueSeverity severity,
        DashboardIssueTarget target,
        DateTimeOffset observedAtUtc)
    {
        return issues.AddOrUpdate(
            sensorId,
            "Dashboard Test Sensor",
            type,
            severity,
            target,
            "Test dashboard issue",
            "Test operational evidence.",
            observedAtUtc);
    }

    /// <summary>
    /// Deterministic TimeProvider used so dynamic priority calculations remain
    /// reproducible across machines and CI.
    /// </summary>
    private sealed class FixedTimeProvider
        : TimeProvider
    {
        private readonly DateTimeOffset _currentTime;

        public FixedTimeProvider(
            DateTimeOffset currentTime)
        {
            _currentTime =
                currentTime;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _currentTime;
        }
    }
}