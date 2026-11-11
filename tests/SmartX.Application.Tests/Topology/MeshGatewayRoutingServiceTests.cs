using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-05 Commit 2: deterministic route reconstruction and classification
/// over hand-verified immutable P3-04 mesh snapshots. These tests use the
/// production BFS and never fabricate a link during sensor registration.
/// </summary>
public sealed class MeshGatewayRoutingServiceTests
{
    [Fact]
    public void GatewayOnlyGraph_HasNoRegisteredRoutesOrDisconnectedComponents()
    {
        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(
            MeshGraphSnapshot.GatewayOnly(32));

        Assert.Equal(32, result.GraphVersion);
        Assert.Empty(result.RegisteredSensors);
        Assert.Empty(result.UnavailableSensors);
        Assert.Empty(result.DisconnectedComponents);
        Assert.Equal(0, result.ReachableSensorCount);
        Assert.Equal(0, result.DisconnectedSensorCount);
        Assert.Equal(0, result.UnavailableSensorCount);
    }

    [Fact]
    public void MultiHopRoute_ReconstructsSensorTowardGatewayNotTheReverse()
    {
        MeshNodeKey first = Sensor(1);
        MeshNodeKey middle = Sensor(2);
        MeshNodeKey leaf = Sensor(3);
        MeshGraphSnapshot graph = Graph(8,
            [Node(first), Node(middle), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, first),
                Link(2, first, middle),
                Link(3, middle, leaf)
            ]);

        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.True(routing.TryGetRouteToGateway(leaf, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(8, route.GraphVersion);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(3, route.HopCount);
        Assert.Equal(new[] { leaf, middle, first, MeshNodeKey.Gateway },
            route.PathToGateway);
        Assert.Equal(3, routing.ReachableSensorCount);
        Assert.Empty(routing.DisconnectedComponents);
    }

    [Fact]
    public void OneHopRoute_IsDirectSensorToGateway()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(
            Graph(4, [Node(sensor)], [Link(1, sensor, MeshNodeKey.Gateway)]));

        Assert.True(routing.TryGetRouteToGateway(sensor, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(1, route.HopCount);
        Assert.Equal(new[] { sensor, MeshNodeKey.Gateway }, route.PathToGateway);
    }

    [Fact]
    public void DisconnectedIslandsAndIsolatedSensors_AreReportedSeparately()
    {
        MeshNodeKey one = Sensor(1);
        MeshNodeKey two = Sensor(2);
        MeshNodeKey three = Sensor(3);
        MeshNodeKey four = Sensor(4);
        MeshNodeKey five = Sensor(5);
        MeshGraphSnapshot graph = Graph(19,
            [Node(one), Node(two), Node(three), Node(four), Node(five)],
            [Link(1, one, two), Link(2, four, three)]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(5, result.RegisteredSensorCount);
        Assert.Equal(0, result.ReachableSensorCount);
        Assert.Equal(5, result.DisconnectedSensorCount);
        Assert.Empty(result.UnavailableSensors);
        Assert.Equal(3, result.DisconnectedComponents.Count);
        Assert.Equal(new[] { one, two }, result.DisconnectedComponents[0].Sensors);
        Assert.Equal(new[] { three, four }, result.DisconnectedComponents[1].Sensors);
        Assert.Equal(new[] { five }, result.DisconnectedComponents[2].Sensors);

        Assert.True(result.TryGetRouteToGateway(five, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Disconnected, route.Status);
        Assert.Null(route.HopCount);
        Assert.Empty(route.PathToGateway);
    }

    [Fact]
    public void OfflineBridge_BlocksDownstreamRoutingWithoutDeletingTopologyLinks()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey downstream = Sensor(2);
        MeshNodeKey extra = Sensor(3);
        MeshGraphSnapshot graph = Graph(31,
            [Node(bridge, SensorStatus.Offline), Node(downstream), Node(extra)],
            [
                Link(1, MeshNodeKey.Gateway, bridge),
                Link(2, bridge, downstream),
                Link(3, downstream, extra)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Empty(result.Reachability.ReachableNodes.Skip(1));
        Assert.Equal(new[] { bridge }, result.UnavailableSensors);
        Assert.Equal(new[] { downstream, extra },
            Assert.Single(result.DisconnectedComponents).Sensors);
        Assert.Equal(1, result.UnavailableSensorCount);
        Assert.Equal(2, result.DisconnectedSensorCount);
        Assert.Equal(3, graph.Links.Count); // Links still exist in the graph.

        Assert.True(result.TryGetRouteToGateway(bridge, out MeshRoute? blocked));
        Assert.NotNull(blocked);
        Assert.Equal(MeshRouteStatus.Unavailable, blocked.Status);

        Assert.True(result.TryGetRouteToGateway(downstream, out MeshRoute? isolated));
        Assert.NotNull(isolated);
        Assert.Equal(MeshRouteStatus.Disconnected, isolated.Status);
    }

    [Theory]
    [InlineData(SensorStatus.Online, MeshRouteStatus.Reachable)]
    [InlineData(SensorStatus.Warning, MeshRouteStatus.Reachable)]
    [InlineData(SensorStatus.Offline, MeshRouteStatus.Unavailable)]
    [InlineData(SensorStatus.Fault, MeshRouteStatus.Unavailable)]
    [InlineData(SensorStatus.Maintenance, MeshRouteStatus.Unavailable)]
    public void SensorHealth_UsesTheSharedForwardingPolicy(
        SensorStatus status,
        MeshRouteStatus expected)
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(
            Graph(23, [Node(sensor, status)],
                [Link(1, MeshNodeKey.Gateway, sensor)]));

        Assert.True(routing.TryGetRouteToGateway(sensor, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(expected, route.Status);

        if (expected == MeshRouteStatus.Reachable)
        {
            Assert.Equal(1, route.HopCount);
        }
        else
        {
            Assert.Null(route.HopCount);
            Assert.Empty(route.PathToGateway);
        }
    }

    [Fact]
    public void DecommissionedSensor_IsUnavailableAndMayNotRetainLinks()
    {
        MeshNodeKey decommissioned = Sensor(1);
        MeshNodeKey unlinked = Sensor(2);
        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(
            Graph(45, [Node(decommissioned, SensorStatus.Decommissioned), Node(unlinked)], []));

        Assert.Equal(new[] { decommissioned }, routing.UnavailableSensors);
        Assert.Equal(new[] { unlinked },
            Assert.Single(routing.DisconnectedComponents).Sensors);
        Assert.True(routing.TryGetRouteToGateway(decommissioned, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Unavailable, route.Status);
    }

    [Fact]
    public void DiamondGraph_EqualHopTieUsesAscendingSensorIdentity()
    {
        MeshNodeKey small = Sensor(1);
        MeshNodeKey large = Sensor(2);
        MeshNodeKey leaf = Sensor(3);
        MeshGraphSnapshot graph = Graph(13,
            [Node(leaf), Node(large), Node(small)],
            [
                Link(4, leaf, large, 0.1),
                Link(3, leaf, small, 100),
                Link(2, MeshNodeKey.Gateway, large, 0.1),
                Link(1, MeshNodeKey.Gateway, small, 100)
            ]);

        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.True(routing.TryGetRouteToGateway(leaf, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(new[] { leaf, small, MeshNodeKey.Gateway },
            route.PathToGateway);
        Assert.Equal(2, route.HopCount);
    }

    [Fact]
    public void BfsIgnoresWeightsAndPrefersFewerHopsOverCheaperLinks()
    {
        MeshNodeKey expensive = Sensor(1);
        MeshNodeKey cheapBridge = Sensor(2);
        MeshGraphSnapshot graph = Graph(7,
            [Node(expensive), Node(cheapBridge)],
            [
                Link(1, MeshNodeKey.Gateway, expensive, 500),
                Link(2, MeshNodeKey.Gateway, cheapBridge, 0.01),
                Link(3, cheapBridge, expensive, 0.01)
            ]);

        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.True(routing.TryGetRouteToGateway(expensive, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(1, route.HopCount);
        Assert.Equal(new[] { expensive, MeshNodeKey.Gateway }, route.PathToGateway);
    }

    [Fact]
    public async Task Service_UsesOneSnapshotPerRouteLookupAndReturnsNullIfUnregistered()
    {
        MeshNodeKey existing = Sensor(1);
        var store = new StaticSnapshotStore(Graph(22, [Node(existing)],
            [Link(1, MeshNodeKey.Gateway, existing)]));
        var service = new MeshTraversalService(store);

        MeshRoute? found = await service.GetSensorRouteAsync(existing.SensorProfileId);
        Assert.NotNull(found);
        Assert.Equal(MeshRouteStatus.Reachable, found.Status);
        Assert.Equal(1, store.ReadCount);

        MeshRoute? missing = await service.GetSensorRouteAsync(Sensor(99).SensorProfileId);
        Assert.Null(missing);
        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task Service_RefreshesRoutingOnEverySnapshotInsteadOfCachingPaths()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, bridge),
            Link(2, bridge, leaf)
        ];
        var store = new StaticSnapshotStore(Graph(2,
            [Node(bridge), Node(leaf)], links));
        var service = new MeshTraversalService(store);

        MeshRoute? first = await service.GetSensorRouteAsync(leaf.SensorProfileId);
        store.Snapshot = Graph(3,
            [Node(bridge, SensorStatus.Offline), Node(leaf)], links);
        MeshRoute? second = await service.GetSensorRouteAsync(leaf.SensorProfileId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MeshRouteStatus.Reachable, first.Status);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway },
            first.PathToGateway);
        Assert.Equal(MeshRouteStatus.Disconnected, second.Status);
        Assert.Empty(second.PathToGateway);
        Assert.Equal(2, first.GraphVersion);
        Assert.Equal(3, second.GraphVersion);
        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task InvalidOrCancelledRouteRequests_DoNotReadTopologyStore()
    {
        var store = new StaticSnapshotStore(MeshGraphSnapshot.GatewayOnly());
        var service = new MeshTraversalService(store);

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            service.GetSensorRouteAsync(Guid.Empty));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetSensorRouteAsync(Sensor(1).SensorProfileId, source.Token));

        Assert.Equal(0, store.ReadCount);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MeshTraversalService.CalculateGatewayRouting(
                MeshGraphSnapshot.GatewayOnly(), source.Token));
        Assert.Throws<ArgumentNullException>(() =>
            MeshTraversalService.CalculateGatewayRouting(null!));
    }

    private static MeshNodeKey Sensor(int ordinal) => MeshNodeKey.ForSensor(
        Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}"));

    private static MeshNode Node(
        MeshNodeKey key,
        SensorStatus status = SensorStatus.Online) => new(key, status);

    private static MeshLink Link(
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double cost = 1) => new(
            Guid.Parse($"10000000-0000-0000-0000-{ordinal:D12}"),
            first, second, cost);

    private static MeshGraphSnapshot Graph(
        long version,
        IReadOnlyList<MeshNode> sensors,
        IReadOnlyList<MeshLink> links) =>
        new(version, [MeshNode.Gateway, .. sensors], links);

    /// <summary>
    /// Pure immutable snapshot substitute. All mutation methods throw so a
    /// routing test cannot silently modify the fixture under traversal.
    /// </summary>
    private sealed class StaticSnapshotStore : IMeshTopologyStore
    {
        public StaticSnapshotStore(MeshGraphSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public MeshGraphSnapshot Snapshot { get; set; }

        public int ReadCount { get; private set; }

        public Task<MeshGraphSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<MeshLink>> GetIncidentLinksAsync(
            MeshNodeKey node,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MeshLink> AddLinkAsync(
            MeshLink link,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveLinkAsync(
            Guid linkId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MeshLink> UpdateLinkCostAsync(
            Guid linkId,
            double simulatedEnergyCost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}