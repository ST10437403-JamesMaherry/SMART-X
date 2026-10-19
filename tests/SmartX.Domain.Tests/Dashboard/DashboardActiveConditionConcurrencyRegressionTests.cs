using System.Collections.Concurrent;

using SmartX.Domain.Dashboard;

namespace SmartX.Domain.Tests.Dashboard;

/// <summary>
/// Regression coverage for the P2-06 HashSet membership invariant under
/// simultaneous observations, operator acknowledgement, and recurrence.
///
/// These tests intentionally use the real Domain collection, not an imitation
/// of the set/dictionary indexes, so failures expose lifecycle regressions.
/// </summary>
public sealed class DashboardActiveConditionConcurrencyRegressionTests
{
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParallelReports_AcrossSensorsAndTypes_RetainOneEpisodePerKey()
    {
        var collection = new DashboardIssueCollection();
        Guid[] devices = Enumerable.Range(0, 6)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        DashboardIssueType[] types =
        [
            DashboardIssueType.TelemetrySpike,
            DashboardIssueType.SensorDisconnected,
            DashboardIssueType.ConfigurationIssue
        ];

        const int reportsPerCondition = 12;
        int totalReports = devices.Length * types.Length * reportsPerCondition;
        var idsByCondition = new ConcurrentDictionary<
            (Guid DeviceId, DashboardIssueType Type),
            ConcurrentBag<Guid>>();

        // Different keys may race with one another, while 12 callbacks for
        // any one key must converge on exactly one active issue episode.
        Parallel.For(0, totalReports, index =>
        {
            int deviceIndex = index / (types.Length * reportsPerCondition);
            int typeIndex = index / reportsPerCondition % types.Length;
            int attempt = index % reportsPerCondition;

            Guid deviceId = devices[deviceIndex];
            DashboardIssueType type = types[typeIndex];

            DashboardIssue issue = Report(
                collection,
                deviceId,
                type,
                attempt % 3 == 0
                    ? DashboardIssueSeverity.Critical
                    : DashboardIssueSeverity.Warning,
                ObservedAtUtc);

            idsByCondition.GetOrAdd(
                (deviceId, type),
                static _ => new ConcurrentBag<Guid>())
                .Add(issue.Id);
        });

        Assert.Equal(devices.Length * types.Length, collection.ActiveConditionCount);
        Assert.Equal(devices.Length * types.Length, collection.Count);
        Assert.Equal(devices.Length * types.Length, idsByCondition.Count);

        IReadOnlyList<DashboardIssue> active =
            collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(1));

        Assert.Equal(devices.Length * types.Length, active.Count);
        Assert.Equal(active.Count, active.Select(issue => issue.Id).Distinct().Count());

        foreach (Guid device in devices)
        {
            foreach (DashboardIssueType type in types)
            {
                Assert.True(collection.ContainsActiveCondition(device, type));

                DashboardIssue row = Assert.Single(
                    active,
                    issue => issue.SensorProfileId == device && issue.Type == type);

                Assert.Equal(reportsPerCondition, row.OccurrenceCount);
                Assert.Equal(DashboardIssueSeverity.Critical, row.Severity);
                Assert.Single(idsByCondition[(device, type)].Distinct());
                Assert.Equal(row.Id, idsByCondition[(device, type)].First());
            }
        }

        // Clearing a key on one sensor must not clear the same condition
        // on another sensor, or a different condition on that same sensor.
        foreach (Guid device in devices)
        {
            Assert.True(collection.Resolve(
                device,
                DashboardIssueType.TelemetrySpike,
                ObservedAtUtc.AddMinutes(2)));
        }

        Assert.Equal(devices.Length * (types.Length - 1), collection.ActiveConditionCount);
        Assert.Equal(devices.Length * (types.Length - 1),
            collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(3)).Count);

        foreach (Guid device in devices)
        {
            Assert.False(collection.ContainsActiveCondition(
                device, DashboardIssueType.TelemetrySpike));
            Assert.True(collection.ContainsActiveCondition(
                device, DashboardIssueType.SensorDisconnected));
            Assert.True(collection.ContainsActiveCondition(
                device, DashboardIssueType.ConfigurationIssue));
        }
    }

    [Fact]
    public void AcknowledgedParallelReports_KeepMembershipUntilRecovery_ThenStartFreshEpisode()
    {
        var collection = new DashboardIssueCollection();
        Guid device = Guid.NewGuid();

        DashboardIssue original = Report(
            collection, device, DashboardIssueType.TelemetrySpike,
            DashboardIssueSeverity.Warning, ObservedAtUtc);

        Assert.True(collection.Acknowledge(
            original.Id, ObservedAtUtc.AddMinutes(1)));

        var repeatedIds = new ConcurrentBag<Guid>();
        Parallel.For(0, 128, index =>
        {
            DashboardIssue repeated = Report(
                collection,
                device,
                DashboardIssueType.TelemetrySpike,
                index % 8 == 0
                    ? DashboardIssueSeverity.Critical
                    : DashboardIssueSeverity.Warning,
                ObservedAtUtc.AddMinutes(2));

            repeatedIds.Add(repeated.Id);
        });

        Assert.Single(repeatedIds.Distinct());
        Assert.Equal(original.Id, repeatedIds.First());
        Assert.Equal(129, original.OccurrenceCount);
        Assert.Equal(DashboardIssueSeverity.Critical, original.Severity);
        Assert.Equal(DashboardIssueState.Acknowledged, original.State);
        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(3)));

        // Acknowledgement alone never removes membership; recovery does.
        Assert.True(collection.Resolve(
            device, DashboardIssueType.TelemetrySpike,
            ObservedAtUtc.AddMinutes(4)));
        Assert.False(collection.ContainsActiveCondition(
            device, DashboardIssueType.TelemetrySpike));
        Assert.Equal(0, collection.ActiveConditionCount);
        Assert.Equal(DashboardIssueState.Resolved, original.State);

        var recurrentIds = new ConcurrentBag<Guid>();
        Parallel.For(0, 64, _ =>
        {
            DashboardIssue recurrence = Report(
                collection,
                device,
                DashboardIssueType.TelemetrySpike,
                DashboardIssueSeverity.Warning,
                ObservedAtUtc.AddMinutes(5));

            recurrentIds.Add(recurrence.Id);
        });

        Guid newEpisodeId = Assert.Single(recurrentIds.Distinct());

        Assert.NotEqual(original.Id, newEpisodeId);
        Assert.Equal(2, collection.Count);
        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.True(collection.ContainsActiveCondition(
            device, DashboardIssueType.TelemetrySpike));

        DashboardIssue active = Assert.Single(
            collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(6)));

        Assert.Equal(newEpisodeId, active.Id);
        Assert.Equal(64, active.OccurrenceCount);
        Assert.Equal(DashboardIssueState.Active, active.State);
        Assert.Equal(ObservedAtUtc.AddMinutes(5), active.FirstObservedAtUtc);

        Assert.Contains(collection.GetPrioritizedSnapshot(ObservedAtUtc.AddMinutes(6)),
            issue => issue.Id == original.Id && issue.State == DashboardIssueState.Resolved);
    }

    [Fact]
    public void ResolvedHistoryCapacity_NeverEvictsUnresolvedHashSetMembers()
    {
        // This capacity limits retained resolved history, not the maximum
        // number of simultaneously active operational conditions.
        var collection = new DashboardIssueCollection(maximumHistory: 2);
        Guid[] devices = Enumerable.Range(0, 4)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        DashboardIssue[] original = devices
            .Select(id => Report(
                collection,
                id,
                DashboardIssueType.SensorDisconnected,
                DashboardIssueSeverity.Warning,
                ObservedAtUtc))
            .ToArray();

        Assert.Equal(4, collection.ActiveConditionCount);
        Assert.Equal(4, collection.Count);

        // Prune only resolved episodes. Neither active membership nor
        // unrelated active issue details may be discarded to meet the cap.
        for (int index = 0; index < 3; index++)
        {
            Assert.True(collection.Resolve(
                devices[index],
                DashboardIssueType.SensorDisconnected,
                ObservedAtUtc.AddMinutes(index + 1)));
        }

        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.True(collection.ContainsActiveCondition(
            devices[3], DashboardIssueType.SensorDisconnected));

        DashboardIssue survivor = Assert.Single(
            collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(5)));
        Assert.Equal(original[3].Id, survivor.Id);
        Assert.True(collection.Count <= 2);

        // The pruned, recovered sensor can activate again using the same
        // stable membership key but with a different issue episode ID.
        DashboardIssue recurrence = Report(
            collection,
            devices[0],
            DashboardIssueType.SensorDisconnected,
            DashboardIssueSeverity.Critical,
            ObservedAtUtc.AddMinutes(6));

        Assert.NotEqual(original[0].Id, recurrence.Id);
        Assert.Equal(2, collection.ActiveConditionCount);
        Assert.True(collection.ContainsActiveCondition(
            devices[0], DashboardIssueType.SensorDisconnected));
        Assert.True(collection.ContainsActiveCondition(
            devices[3], DashboardIssueType.SensorDisconnected));
        Assert.Equal(2,
            collection.GetActiveSnapshot(ObservedAtUtc.AddMinutes(7)).Count);
    }

    /// <summary>
    /// Produces one legitimate condition report using the existing Domain
    /// validation and priority rules. No reflection or index manipulation.
    /// </summary>
    private static DashboardIssue Report(
        DashboardIssueCollection collection,
        Guid deviceId,
        DashboardIssueType type,
        DashboardIssueSeverity severity,
        DateTimeOffset observedAtUtc)
    {
        DashboardIssueTarget target = type switch
        {
            DashboardIssueType.ConfigurationIssue => DashboardIssueTarget.Attachments,
            DashboardIssueType.SensorDisconnected => DashboardIssueTarget.Sensor,
            _ => DashboardIssueTarget.Telemetry
        };

        return collection.AddOrUpdate(
            deviceId,
            "P2-06 concurrency sensor",
            type,
            severity,
            target,
            "Unresolved operational condition",
            "Exact repeated observation supplied by a parallel reporter.",
            observedAtUtc);
    }
}