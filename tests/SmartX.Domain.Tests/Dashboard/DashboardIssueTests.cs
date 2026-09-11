using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Tests.Dashboard;

public sealed class DashboardIssueTests
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
    public void Create_WhenValid_CreatesActiveIssue()
    {
        Guid sensorId = Guid.NewGuid();

        DashboardIssue issue =
            DashboardIssue.Create(
                sensorId,
                "Temperature Sensor",
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Telemetry,
                "Telemetry spike detected",
                "The latest reading changed rapidly.",
                BaseTime);

        Assert.NotEqual(
            Guid.Empty,
            issue.Id);

        Assert.Equal(
            sensorId,
            issue.SensorProfileId);

        Assert.Equal(
            DashboardIssueState.Active,
            issue.State);

        Assert.Equal(
            1,
            issue.OccurrenceCount);

        Assert.Equal(
            BaseTime,
            issue.FirstObservedAtUtc);

        Assert.Equal(
            BaseTime,
            issue.LastObservedAtUtc);
    }

    [Fact]
    public void RecordOccurrence_WhenRepeated_IncrementsCount()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        issue.RecordOccurrence(
            "Temperature Sensor",
            DashboardIssueSeverity.Warning,
            DashboardIssueTarget.Telemetry,
            "Telemetry spike detected",
            "Another rapid change was detected.",
            BaseTime.AddMinutes(1));

        Assert.Equal(
            2,
            issue.OccurrenceCount);

        Assert.Equal(
            BaseTime.AddMinutes(1),
            issue.LastObservedAtUtc);
    }

    [Fact]
    public void RecordOccurrence_WhenSeverityHigher_EscalatesSeverity()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        issue.RecordOccurrence(
            "Temperature Sensor",
            DashboardIssueSeverity.Critical,
            DashboardIssueTarget.Telemetry,
            "Critical telemetry spike",
            "The telemetry delta exceeded the critical threshold.",
            BaseTime.AddMinutes(1));

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            issue.Severity);
    }

    [Fact]
    public void RecordOccurrence_WhenSeverityLower_DoesNotDowngradeIssue()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Critical);

        issue.RecordOccurrence(
            "Temperature Sensor",
            DashboardIssueSeverity.Warning,
            DashboardIssueTarget.Telemetry,
            "Telemetry spike detected",
            "The latest observation is less severe.",
            BaseTime.AddMinutes(1));

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            issue.Severity);
    }

    [Fact]
    public void Acknowledge_WhenActive_PreservesIssueButChangesState()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        issue.Acknowledge(
            BaseTime.AddMinutes(2));

        Assert.Equal(
            DashboardIssueState.Acknowledged,
            issue.State);

        Assert.Equal(
            BaseTime.AddMinutes(2),
            issue.AcknowledgedAtUtc);

        Assert.Null(
            issue.ResolvedAtUtc);
    }

    [Fact]
    public void Resolve_WhenActive_MarksConditionResolved()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        issue.Resolve(
            BaseTime.AddMinutes(4));

        Assert.Equal(
            DashboardIssueState.Resolved,
            issue.State);

        Assert.Equal(
            BaseTime.AddMinutes(4),
            issue.ResolvedAtUtc);

        Assert.Equal(
            0,
            issue.CalculatePriorityScore(
                BaseTime.AddMinutes(5)));
    }

    [Fact]
    public void CalculatePriorityScore_WhenRepeated_IncreasesPriority()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        int initialScore =
            issue.CalculatePriorityScore(
                BaseTime);

        issue.RecordOccurrence(
            "Temperature Sensor",
            DashboardIssueSeverity.Warning,
            DashboardIssueTarget.Telemetry,
            "Telemetry spike detected",
            "Repeated spike.",
            BaseTime.AddMinutes(1));

        int repeatedScore =
            issue.CalculatePriorityScore(
                BaseTime.AddMinutes(1));

        Assert.True(
            repeatedScore > initialScore);
    }

    [Fact]
    public void CalculatePriorityScore_WhenAcknowledged_ReducesPriority()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        int activeScore =
            issue.CalculatePriorityScore(
                BaseTime.AddMinutes(1));

        issue.Acknowledge(
            BaseTime.AddMinutes(1));

        int acknowledgedScore =
            issue.CalculatePriorityScore(
                BaseTime.AddMinutes(1));

        Assert.True(
            acknowledgedScore < activeScore);
    }

    [Fact]
    public void CalculatePriorityScore_WhenDisconnectPersists_IncreasesPriority()
    {
        DashboardIssue issue =
            DashboardIssue.Create(
                Guid.NewGuid(),
                "Gateway Sensor",
                DashboardIssueType.SensorDisconnected,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Sensor,
                "Sensor disconnected",
                "The sensor is no longer online.",
                BaseTime);

        int initialScore =
            issue.CalculatePriorityScore(
                BaseTime);

        int laterScore =
            issue.CalculatePriorityScore(
                BaseTime.AddMinutes(30));

        Assert.True(
            laterScore > initialScore);
    }

    [Fact]
    public void RecordOccurrence_WhenResolved_Throws()
    {
        DashboardIssue issue =
            CreateIssue(
                DashboardIssueSeverity.Warning);

        issue.Resolve(
            BaseTime.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () =>
                issue.RecordOccurrence(
                    "Temperature Sensor",
                    DashboardIssueSeverity.Warning,
                    DashboardIssueTarget.Telemetry,
                    "Telemetry spike detected",
                    "Repeated spike.",
                    BaseTime.AddMinutes(2)));
    }

    private static DashboardIssue CreateIssue(
        DashboardIssueSeverity severity)
    {
        return DashboardIssue.Create(
            Guid.NewGuid(),
            "Temperature Sensor",
            DashboardIssueType.TelemetrySpike,
            severity,
            DashboardIssueTarget.Telemetry,
            "Telemetry spike detected",
            "The latest reading changed rapidly.",
            BaseTime);
    }
}