using SmartX.Application.Topology;
using SmartX.Contracts.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-10 Commit 2. Tests the real BFS-backed insight projector using immutable,
/// hand-verified graph snapshots. No artificial recommendations, commands,
/// search history, or simulated telemetry are created by these tests.
/// </summary>
public sealed class TopologyInsightServiceTests
{
    [Fact]
    public async Task FirstRead_ExposesMultiSensorGatewayFailure_WithoutTraining()
    {
        MeshNodeKey first = Sensor(1);
        MeshNodeKey second = Sensor(2);
        var store = new SnapshotStore(Graph(11,
            [Node(first), Node(second)], []));
        var clock = new ManualClock();
        var service = new TopologyInsightService(store, clock);

        TopologyInsightsResponse response = await service.GetCurrentAsync();

        Assert.Equal(11, response.GraphVersion);
        Assert.Equal(2, response.RegisteredSensorCount);
        Assert.Equal(2, response.DisconnectedSensorCount);
        TopologyInsightResponse warning = Assert.Single(response.Insights);
        Assert.Equal("topology:gateway-disconnected", warning.Key);
        Assert.Equal("GatewayDisconnected", warning.Kind);
        Assert.Equal("Critical", warning.Severity);
        Assert.Equal("CurrentTopologyBfs", warning.Source);
        Assert.False(warning.IsLearned);
        Assert.True(warning.IsCurrent);
        Assert.Null(warning.ExpiresAtUtc);
        Assert.Equal(2, warning.AffectedRegisteredSensorCount);
        Assert.Equal(new[] { first.SensorProfileId, second.SensorProfileId },
            warning.AffectedSensorIds);
        Assert.Equal(0, warning.AdditionalAffectedSensorCount);
        Assert.Contains("2 disconnected component", warning.Explanation);
        Assert.Contains("Inspect", warning.RecommendedAction);
        Assert.Equal(clock.GetUtcNow(), response.ProjectionComputedAtUtc);
        Assert.Equal(clock.GetUtcNow(), warning.FirstObservedAtUtc);
    }

    [Fact]
    public async Task UnlinkedOfflineInventory_DoesNotProduceAForwardingHealthAlert()
    {
        MeshNodeKey offline = Sensor(1);
        var store = new SnapshotStore(Graph(1,
            [Node(offline, SensorStatus.Offline)], []));
        var service = new TopologyInsightService(store, new ManualClock());

        TopologyInsightsResponse unlinked = await service.GetCurrentAsync();
        Assert.Equal(1, unlinked.UnavailableSensorCount);
        Assert.Empty(unlinked.Insights);

        store.Snapshot = Graph(2,
            [Node(offline, SensorStatus.Offline)],
            [Link(1, MeshNodeKey.Gateway, offline)]);
        TopologyInsightsResponse linked = await service.GetCurrentAsync();

        TopologyInsightResponse health = Assert.Single(linked.Insights);
        Assert.Equal("NonForwardingDevices", health.Kind);
        Assert.Equal("CurrentTopologySensorStatus", health.Source);
        Assert.Equal(offline.SensorProfileId, Assert.Single(health.AffectedSensorIds));
        Assert.False(health.IsLearned);
    }

    [Fact]
    public async Task OfflineBridge_TriggersDistinctAlerts_ThenTrueRecoveryExpires()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, bridge),
            Link(2, bridge, leaf)
        ];
        var clock = new ManualClock();
        var store = new SnapshotStore(Graph(1,
            [Node(bridge), Node(leaf)], links));
        var service = new TopologyInsightService(store, clock);

        Assert.Empty((await service.GetCurrentAsync()).Insights);

        clock.Advance(TimeSpan.FromSeconds(3));
        store.Snapshot = Graph(2,
            [Node(bridge, SensorStatus.Offline), Node(leaf)], links);
        TopologyInsightsResponse failed = await service.GetCurrentAsync();
        Assert.Equal(1, failed.DisconnectedSensorCount);
        Assert.Equal(1, failed.UnavailableSensorCount);
        Assert.Equal(2, failed.Insights.Count);
        TopologyInsightResponse disconnection = Assert.Single(failed.Insights,
            item => item.Kind == "GatewayDisconnected");
        Assert.Equal("Warning", disconnection.Severity);
        Assert.Equal(leaf.SensorProfileId,
            Assert.Single(disconnection.AffectedSensorIds));
        TopologyInsightResponse unavailable = Assert.Single(failed.Insights,
            item => item.Kind == "NonForwardingDevices");
        Assert.Equal(bridge.SensorProfileId,
            Assert.Single(unavailable.AffectedSensorIds));
        Assert.Equal(links.Length, store.Snapshot.Links.Count);

        // Repeated reads coalesce instead of allocating another alert event.
        TopologyInsightsResponse repeated = await service.GetCurrentAsync();
        Assert.Equal(failed.ProjectionComputedAtUtc,
            repeated.ProjectionComputedAtUtc);
        Assert.Equal(disconnection.FirstObservedAtUtc,
            repeated.Insights.Single(item => item.Kind == "GatewayDisconnected")
                .FirstObservedAtUtc);

        clock.Advance(TimeSpan.FromSeconds(5));
        store.Snapshot = Graph(3, [Node(bridge), Node(leaf)], links);
        TopologyInsightsResponse recovered = await service.GetCurrentAsync();
        TopologyInsightResponse restoration = Assert.Single(recovered.Insights);
        Assert.Equal("GatewayConnectivityRestored", restoration.Kind);
        Assert.Equal("Info", restoration.Severity);
        Assert.False(restoration.IsLearned);
        Assert.False(restoration.IsCurrent);
        Assert.Equal(leaf.SensorProfileId,
            Assert.Single(restoration.AffectedSensorIds));
        Assert.Equal(3, restoration.EvidenceGraphVersion);
        Assert.Equal(2, recovered.ReachableSensorCount);
        Assert.Equal(0, recovered.DisconnectedSensorCount);
        Assert.Equal(clock.GetUtcNow().Add(TopologyInsightService.RecoveryVisibleFor),
            restoration.ExpiresAtUtc);

        clock.Advance(TopologyInsightService.RecoveryVisibleFor +
            TimeSpan.FromMilliseconds(1));
        Assert.Empty((await service.GetCurrentAsync()).Insights);
        Assert.Equal(2, store.Snapshot.Links.Count);
    }

    [Fact]
    public async Task FailureBecomingUnavailable_IsNotMistakenForRecovery()
    {
        MeshNodeKey sensor = Sensor(1);
        var store = new SnapshotStore(Graph(4, [Node(sensor)], []));
        var service = new TopologyInsightService(store, new ManualClock());
        Assert.Equal("GatewayDisconnected",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);

        store.Snapshot = Graph(5,
            [Node(sensor, SensorStatus.Offline)], []);
        TopologyInsightsResponse result = await service.GetCurrentAsync();
        Assert.Equal(0, result.DisconnectedSensorCount);
        Assert.Equal(1, result.UnavailableSensorCount);
        Assert.DoesNotContain(result.Insights,
            item => item.Kind == "GatewayConnectivityRestored");
    }

    [Fact]
    public async Task RevisionsReuseStableActiveKeysAndObservationTime()
    {
        MeshNodeKey sensor = Sensor(1);
        var clock = new ManualClock();
        var store = new SnapshotStore(Graph(1, [Node(sensor)], []));
        var service = new TopologyInsightService(store, clock);

        TopologyInsightsResponse first = await service.GetCurrentAsync();
        clock.Advance(TimeSpan.FromSeconds(12));
        TopologyInsightsResponse cached = await service.GetCurrentAsync();
        Assert.Equal(first.ProjectionComputedAtUtc, cached.ProjectionComputedAtUtc);
        Assert.Equal(first.Insights[0].FirstObservedAtUtc,
            cached.Insights[0].FirstObservedAtUtc);
        Assert.True(cached.EvaluatedAtUtc > first.EvaluatedAtUtc);
        Assert.Single(cached.Insights);

        store.Snapshot = Graph(2, [Node(sensor)], []);
        TopologyInsightsResponse revised = await service.GetCurrentAsync();
        Assert.Equal(2, revised.GraphVersion);
        Assert.Equal(2, revised.Insights[0].EvidenceGraphVersion);
        Assert.Equal(first.Insights[0].Key, revised.Insights[0].Key);
        Assert.Equal(first.Insights[0].FirstObservedAtUtc,
            revised.Insights[0].FirstObservedAtUtc);
        Assert.Equal(clock.GetUtcNow(), revised.ProjectionComputedAtUtc);
    }

    [Fact]
    public async Task FlappingRestoration_IsRateLimitedButCurrentFailureIsNot()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshLink[] links = [Link(1, MeshNodeKey.Gateway, sensor)];
        var clock = new ManualClock();
        var store = new SnapshotStore(Graph(1, [Node(sensor)], links));
        var service = new TopologyInsightService(store, clock);
        Assert.Empty((await service.GetCurrentAsync()).Insights);

        store.Snapshot = Graph(2, [Node(sensor)], []);
        Assert.Equal("GatewayDisconnected",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);

        store.Snapshot = Graph(3, [Node(sensor)], links);
        Assert.Equal("GatewayConnectivityRestored",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);

        clock.Advance(TimeSpan.FromSeconds(5));
        store.Snapshot = Graph(4, [Node(sensor)], []);
        Assert.Equal("GatewayDisconnected",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);

        clock.Advance(TimeSpan.FromSeconds(5));
        store.Snapshot = Graph(5, [Node(sensor)], links);
        Assert.Empty((await service.GetCurrentAsync()).Insights);

        clock.Advance(TopologyInsightService.RecoveryCooldown);
        store.Snapshot = Graph(6, [Node(sensor)], []);
        Assert.Equal("GatewayDisconnected",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);
        store.Snapshot = Graph(7, [Node(sensor)], links);
        Assert.Equal("GatewayConnectivityRestored",
            Assert.Single((await service.GetCurrentAsync()).Insights).Kind);
    }

    [Fact]
    public async Task LargeDisconnectedGraph_CapsPayloadButPreservesActualTotals()
    {
        MeshNode[] nodes = Enumerable.Range(1, 35)
            .Select(index => Node(Sensor(index)))
            .ToArray();
        var service = new TopologyInsightService(
            new SnapshotStore(Graph(99, nodes, [])), new ManualClock());

        TopologyInsightsResponse result = await service.GetCurrentAsync();
        TopologyInsightResponse insight = Assert.Single(result.Insights);
        Assert.Equal(35, result.DisconnectedSensorCount);
        Assert.Equal(35, insight.AffectedRegisteredSensorCount);
        Assert.Equal(TopologyInsightService.MaximumAffectedIdsPerInsight,
            insight.AffectedSensorIds.Count);
        Assert.Equal(19, insight.AdditionalAffectedSensorCount);
        Assert.Equal(insight.AffectedSensorIds.Count,
            insight.AffectedSensorIds.Distinct().Count());
        Assert.True(result.Insights.Count <=
            TopologyInsightService.MaximumInsightsPerResponse);
    }

    [Fact]
    public async Task LargeRecovery_KeepsAccurateTotalWithBoundedIdentityList()
    {
        MeshNodeKey[] keys = Enumerable.Range(1, 35).Select(Sensor).ToArray();
        MeshNode[] nodes = keys.Select(NodeOnline).ToArray();
        var store = new SnapshotStore(Graph(1, nodes, []));
        var service = new TopologyInsightService(store, new ManualClock());

        Assert.Equal(35, (await service.GetCurrentAsync()).DisconnectedSensorCount);

        MeshLink[] links = keys.Select((key, index) =>
            Link(index + 1, MeshNodeKey.Gateway, key)).ToArray();
        store.Snapshot = Graph(2, nodes, links);
        TopologyInsightsResponse response = await service.GetCurrentAsync();

        TopologyInsightResponse recovered = Assert.Single(response.Insights);
        Assert.Equal("GatewayConnectivityRestored", recovered.Kind);
        Assert.Equal(35, recovered.AffectedRegisteredSensorCount);
        Assert.Equal(TopologyInsightService.MaximumAffectedIdsPerInsight,
            recovered.AffectedSensorIds.Count);
        Assert.Equal(19, recovered.AdditionalAffectedSensorCount);
    }

    [Fact]
    public async Task CancelledRead_DoesNotChangeVersionedProjection()
    {
        MeshNodeKey sensor = Sensor(1);
        var store = new SnapshotStore(Graph(1, [Node(sensor)], []));
        var service = new TopologyInsightService(store, new ManualClock());
        Assert.Equal(1, (await service.GetCurrentAsync()).GraphVersion);

        store.Snapshot = Graph(2, [Node(sensor)], []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetCurrentAsync(cancellation.Token));

        // A subsequent uncancelled read can observe the new revision.
        TopologyInsightsResponse result = await service.GetCurrentAsync();
        Assert.Equal(2, result.GraphVersion);
        Assert.Equal("GatewayDisconnected", Assert.Single(result.Insights).Kind);
    }

    private static MeshNode NodeOnline(MeshNodeKey key) =>
        Node(key, SensorStatus.Online);

    [Fact]
    public async Task ConcurrentReadsOfSameRevision_ReturnOneStableProjection()
    {
        MeshNodeKey sensor = Sensor(1);
        var clock = new ManualClock();
        var store = new SnapshotStore(Graph(41, [Node(sensor)], []));
        var service = new TopologyInsightService(store, clock);

        TopologyInsightsResponse[] results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => service.GetCurrentAsync()));

        Assert.All(results, result =>
        {
            Assert.Equal(41, result.GraphVersion);
            Assert.Single(result.Insights);
            Assert.Equal(results[0].ProjectionComputedAtUtc,
                result.ProjectionComputedAtUtc);
            Assert.Equal(results[0].Insights[0].FirstObservedAtUtc,
                result.Insights[0].FirstObservedAtUtc);
        });
    }

    private static MeshNodeKey Sensor(int value) => MeshNodeKey.ForSensor(
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private static MeshNode Node(
        MeshNodeKey key,
        SensorStatus status = SensorStatus.Online) => new(key, status);

    private static MeshLink Link(
        int value,
        MeshNodeKey first,
        MeshNodeKey second) => new(
            Guid.Parse($"10000000-0000-0000-0000-{value:D12}"),
            first, second, 1d);

    private static MeshGraphSnapshot Graph(
        long version,
        IReadOnlyList<MeshNode> sensors,
        IReadOnlyList<MeshLink> links) => new(
            version, [MeshNode.Gateway, .. sensors], links);

    /// <summary>
    /// Only GetSnapshotAsync is used by advisory reads. Mutations are
    /// unsupported, proving no write dependency or implicit actuation.
    /// </summary>
    private sealed class SnapshotStore(MeshGraphSnapshot snapshot) : IMeshTopologyStore
    {
        public MeshGraphSnapshot Snapshot { get; set; } = snapshot;

        public Task<MeshGraphSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<MeshLink>> GetIncidentLinksAsync(
            MeshNodeKey node, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Advisory reads cannot query mutable links.");

        public Task<MeshLink> AddLinkAsync(
            MeshLink link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Advisory reads cannot add links.");

        public Task<bool> RemoveLinkAsync(
            Guid linkId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Advisory reads cannot remove links.");

        public Task<MeshLink> UpdateLinkCostAsync(
            Guid linkId,
            double simulatedEnergyCost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Advisory reads cannot change costs.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}