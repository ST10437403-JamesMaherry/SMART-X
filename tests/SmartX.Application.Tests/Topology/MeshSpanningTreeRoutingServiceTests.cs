using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-06 Commit 2: hand-verified Prim paths and connectivity projections.
/// Tree routes must follow selected weighted parents, whereas BFS continues
/// to answer the separate fewest-hop question on the same input snapshot.
/// </summary>
public sealed class MeshSpanningTreeRoutingServiceTests
{
    [Fact]
    public void GatewayOnlyGraph_HasNoRegisteredSensorsAndZeroCost()
    {
        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                MeshGraphSnapshot.GatewayOnly(47));

        Assert.Equal(47, result.GraphVersion);
        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
        Assert.Equal(0, result.SelectedEdgeCount);
        Assert.Equal(0, result.RegisteredSensorCount);
        Assert.Empty(result.DisconnectedComponents);
        Assert.Empty(result.UnavailableSensors);
        Assert.False(result.TryGetTreeRouteToGateway(Sensor(1), out _));
    }

    [Fact]
    public void WeightedGraph_TreePathsFollowExactSelectedEdgesAndCosts()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Graph(18,
            [Node(c), Node(a), Node(b)],
            [
                Link(6, MeshNodeKey.Gateway, c, 20),
                Link(4, a, c, 10),
                Link(2, MeshNodeKey.Gateway, b, 4),
                Link(5, b, c, 3),
                Link(3, a, b, 2),
                Link(1, MeshNodeKey.Gateway, a, 1)
            ]);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(18, result.GraphVersion);
        Assert.Equal(6d, result.TotalSimulatedEnergyCost);
        Assert.Equal(3, result.SelectedEdgeCount);
        Assert.Equal(3, result.ReachableSensorCount);
        Assert.Equal(0, result.DisconnectedSensorCount);
        Assert.Equal(0, result.UnavailableSensorCount);
        Assert.Equal(3, result.Tree.ReachedNodes.Count - 1);
        Assert.Equal(new[] { LinkId(1), LinkId(3), LinkId(5) },
            result.Tree.SelectedBranches.Select(branch => branch.Link.Id));

        Assert.True(result.TryGetTreeRouteToGateway(c, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(3, route.HopCount);
        Assert.Equal(6d, route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { c, b, a, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Equal(new[] { LinkId(5), LinkId(3), LinkId(1) },
            route.SelectedLinksToGateway.Select(link => link.Id));

        Assert.True(result.TryGetTreeRouteToGateway(a, out MeshTreeRoute? direct));
        Assert.NotNull(direct);
        Assert.Equal(1d, direct.AccumulatedSimulatedEnergyCost);
        Assert.Equal(1, direct.HopCount);
        Assert.Equal(6d, result.TotalSimulatedEnergyCost);
    }

    [Fact]
    public void TreePathCanDifferFromBfsFewestHopPath()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot graph = Graph(25,
            [Node(a), Node(b)],
            [
                Link(1, MeshNodeKey.Gateway, a, 100),
                Link(2, MeshNodeKey.Gateway, b, 1),
                Link(3, b, a, 1)
            ]);

        MeshGatewaySpanningTreeRouting prim =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);
        MeshGatewayRouting bfs = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.True(prim.TryGetTreeRouteToGateway(a, out MeshTreeRoute? treeRoute));
        Assert.True(bfs.TryGetRouteToGateway(a, out MeshRoute? bfsRoute));
        Assert.NotNull(treeRoute);
        Assert.NotNull(bfsRoute);
        Assert.Equal(new[] { a, b, MeshNodeKey.Gateway }, treeRoute.PathToGateway);
        Assert.Equal(2, treeRoute.HopCount);
        Assert.Equal(2d, treeRoute.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { a, MeshNodeKey.Gateway }, bfsRoute.PathToGateway);
        Assert.Equal(1, bfsRoute.HopCount);
        Assert.Equal(2d, prim.TotalSimulatedEnergyCost);
    }

    [Fact]
    public void MstPathCost_IsNotGuaranteedToBeShortestWeightedSensorRoute()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Graph(26,
            [Node(a), Node(b), Node(c)],
            [
                Link(1, MeshNodeKey.Gateway, a, 1),
                Link(2, a, b, 1),
                Link(3, b, c, 1),
                Link(4, MeshNodeKey.Gateway, c, 2.5)
            ]);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(3d, result.TotalSimulatedEnergyCost);
        Assert.True(result.TryGetTreeRouteToGateway(c, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(new[] { c, b, a, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Equal(3d, route.AccumulatedSimulatedEnergyCost);

        // A real configured GW-C link costs 2.5, but it is NOT selected in
        // the globally cheapest spanning backbone (total cost 3).
        // Do not mislabel this tree route as Dijkstra's shortest-cost path.
        Assert.Equal(2.5d,
            graph.Links.Single(link => link.Id == LinkId(4)).SimulatedEnergyCost);
        Assert.DoesNotContain(result.Tree.SelectedLinks,
            link => link.Id == LinkId(4));
    }

    [Fact]
    public void DisconnectedIslandsAndIsolatedSensor_AreClassifiedIndividually()
    {
        MeshNodeKey connected = Sensor(1);
        MeshNodeKey islandA = Sensor(2);
        MeshNodeKey islandB = Sensor(3);
        MeshNodeKey isolated = Sensor(4);
        MeshGraphSnapshot graph = Graph(81,
            [Node(connected), Node(islandA), Node(islandB), Node(isolated)],
            [
                Link(1, MeshNodeKey.Gateway, connected, 3),
                Link(2, islandA, islandB, 1)
            ]);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(3d, result.TotalSimulatedEnergyCost);
        Assert.Equal(4, result.RegisteredSensorCount);
        Assert.Equal(1, result.ReachableSensorCount);
        Assert.Equal(3, result.DisconnectedSensorCount);
        Assert.Equal(0, result.UnavailableSensorCount);
        Assert.Equal(2, result.DisconnectedComponents.Count);
        Assert.Equal(new[] { islandA, islandB },
            result.DisconnectedComponents[0].Sensors);
        Assert.Equal(new[] { isolated }, result.DisconnectedComponents[1].Sensors);

        Assert.True(result.TryGetTreeRouteToGateway(islandA, out MeshTreeRoute? island));
        Assert.NotNull(island);
        Assert.Equal(MeshRouteStatus.Disconnected, island.Status);
        Assert.Null(island.AccumulatedSimulatedEnergyCost);
        Assert.Empty(island.SelectedLinksToGateway);
        Assert.Empty(island.PathToGateway);

        Assert.True(result.TryGetTreeRouteToGateway(isolated, out MeshTreeRoute? lone));
        Assert.NotNull(lone);
        Assert.Equal(MeshRouteStatus.Disconnected, lone.Status);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public void UnavailableBridge_IsNotUsedAndDownstreamIsDisconnected(
        SensorStatus unavailableStatus)
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Graph(32,
            [Node(bridge, unavailableStatus), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge, 1),
                Link(2, bridge, leaf, 1)
            ]);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
        Assert.Empty(result.Tree.SelectedLinks);
        Assert.Equal(new[] { bridge }, result.UnavailableSensors);
        Assert.Equal(new[] { leaf }, Assert.Single(result.DisconnectedComponents).Sensors);
        Assert.True(result.TryGetTreeRouteToGateway(bridge, out MeshTreeRoute? unavailable));
        Assert.NotNull(unavailable);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.AccumulatedSimulatedEnergyCost);
        Assert.Empty(unavailable.SelectedLinksToGateway);
        Assert.True(result.TryGetTreeRouteToGateway(leaf, out MeshTreeRoute? isolated));
        Assert.NotNull(isolated);
        Assert.Equal(MeshRouteStatus.Disconnected, isolated.Status);
        Assert.Empty(isolated.PathToGateway);

        // Configured links still exist in the source graph: they were NOT
        // deleted or silently treated as working radio connections.
        Assert.Equal(2, graph.Links.Count);
    }

    [Fact]
    public void WarningRelay_RemainsEligibleForGatewayTreeAndWeightedPath()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Graph(14,
            [Node(bridge, SensorStatus.Warning), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge, 2),
                Link(2, bridge, leaf, 5)
            ]);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(7d, result.TotalSimulatedEnergyCost);
        Assert.True(result.TryGetTreeRouteToGateway(leaf, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(7d, route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, route.PathToGateway);
    }

    [Fact]
    public void DecommissionedUnlinkedSensor_IsUnavailableRatherThanDisconnected()
    {
        MeshNodeKey retired = Sensor(1);
        MeshGraphSnapshot graph = Graph(33,
            [Node(retired, SensorStatus.Decommissioned)], []);

        MeshGatewaySpanningTreeRouting result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(new[] { retired }, result.UnavailableSensors);
        Assert.Equal(1, result.UnavailableSensorCount);
        Assert.Empty(result.DisconnectedComponents);
        Assert.True(result.TryGetTreeRouteToGateway(retired, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Unavailable, route.Status);
    }

    [Fact]
    public void EqualCostDiamonds_ProduceStableRoutesAfterPermutingInputOrder()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshNode[] nodes = [Node(a), Node(b), Node(c)];
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, MeshNodeKey.Gateway, b, 1),
            Link(3, a, c, 1),
            Link(4, b, c, 1)
        ];

        MeshGatewaySpanningTreeRouting first =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                Graph(44, nodes, links));
        MeshGatewaySpanningTreeRouting second =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                Graph(44, nodes.Reverse(), links.Reverse()));

        Assert.True(first.TryGetTreeRouteToGateway(c, out MeshTreeRoute? firstRoute));
        Assert.True(second.TryGetTreeRouteToGateway(c, out MeshTreeRoute? secondRoute));
        Assert.NotNull(firstRoute);
        Assert.NotNull(secondRoute);
        Assert.Equal(new[] { c, a, MeshNodeKey.Gateway }, firstRoute.PathToGateway);
        Assert.Equal(firstRoute.PathToGateway, secondRoute.PathToGateway);
        Assert.Equal(firstRoute.AccumulatedSimulatedEnergyCost,
            secondRoute.AccumulatedSimulatedEnergyCost);
        Assert.Equal(first.Tree.SelectedLinks.Select(link => link.Id),
            second.Tree.SelectedLinks.Select(link => link.Id));
    }

    [Fact]
    public void UnknownTarget_IsNotMisreportedAsAnUnavailableOrDisconnectedSensor()
    {
        MeshNodeKey known = Sensor(1);
        MeshGatewaySpanningTreeRouting routing =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                Graph(5, [Node(known)], []));

        Assert.False(routing.TryGetTreeRouteToGateway(Sensor(99), out MeshTreeRoute? missing));
        Assert.Null(missing);
        Assert.True(routing.TryGetTreeRouteToGateway(known, out MeshTreeRoute? disconnected));
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);
    }

    [Fact]
    public void PreCancelledCalculation_DoesNotPublishPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                MeshGraphSnapshot.GatewayOnly(), cancellation.Token));
    }

    [Fact]
    public async Task ServiceUsesOneSnapshotReadPerProjectionAndNoStaleCache()
    {
        MeshNodeKey a = Sensor(1);
        var source = new MutableSnapshotStore(Graph(7,
            [Node(a)], [Link(1, MeshNodeKey.Gateway, a, 4)]));
        var service = new MeshSpanningTreeService(source);

        MeshGatewaySpanningTreeRouting first =
            await service.GetGatewaySpanningTreeRoutingAsync();

        source.Snapshot = Graph(8, [Node(a)], []);
        MeshGatewaySpanningTreeRouting second =
            await service.GetGatewaySpanningTreeRoutingAsync();

        Assert.Equal(2, source.ReadCount);
        Assert.Equal(7, first.GraphVersion);
        Assert.Equal(8, second.GraphVersion);
        Assert.Equal(4d, first.TotalSimulatedEnergyCost);
        Assert.Equal(0d, second.TotalSimulatedEnergyCost);
        Assert.True(first.TryGetTreeRouteToGateway(a, out MeshTreeRoute? oldRoute));
        Assert.True(second.TryGetTreeRouteToGateway(a, out MeshTreeRoute? newRoute));
        Assert.NotNull(oldRoute);
        Assert.NotNull(newRoute);
        Assert.Equal(MeshRouteStatus.Reachable, oldRoute.Status);
        Assert.Equal(MeshRouteStatus.Disconnected, newRoute.Status);
        Assert.Equal(4d, oldRoute.AccumulatedSimulatedEnergyCost);
        Assert.Null(newRoute.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public async Task SingleSensorLookup_ReturnsCorrectTypeAndUnknownReturnsNull()
    {
        MeshNodeKey a = Sensor(1);
        var source = new MutableSnapshotStore(Graph(19,
            [Node(a)], [Link(1, MeshNodeKey.Gateway, a, 3)]));
        var service = new MeshSpanningTreeService(source);

        MeshTreeRoute? connected = await service.GetSensorTreeRouteAsync(a.SensorProfileId);
        MeshTreeRoute? missing = await service.GetSensorTreeRouteAsync(Sensor(90).SensorProfileId);

        Assert.NotNull(connected);
        Assert.Equal(3d, connected.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { a, MeshNodeKey.Gateway }, connected.PathToGateway);
        Assert.Null(missing);
        Assert.Equal(2, source.ReadCount);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            service.GetSensorTreeRouteAsync(Guid.Empty));
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task CancelledRead_PropagatesCancellationWithoutQueryingStore()
    {
        var source = new MutableSnapshotStore(MeshGraphSnapshot.GatewayOnly());
        var service = new MeshSpanningTreeService(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetGatewaySpanningTreeRoutingAsync(cancellation.Token));
        Assert.Equal(0, source.ReadCount);
    }

    private static MeshGraphSnapshot Graph(
        long version,
        IEnumerable<MeshNode> sensors,
        IEnumerable<MeshLink> links) =>
        new(version, new[] { MeshNode.Gateway }.Concat(sensors), links);

    private static MeshNode Node(
        MeshNodeKey sensor, SensorStatus status = SensorStatus.Online) =>
        new(sensor, status);

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:000000000000}"));

    private static Guid LinkId(int ordinal) =>
        Guid.Parse($"10000000-0000-0000-0000-{ordinal:000000000000}");

    private static MeshLink Link(
        int ordinal, MeshNodeKey first, MeshNodeKey second, double cost) =>
        new(LinkId(ordinal), first, second, cost);

    /// <summary>
    /// Test-only store allows only immutable snapshot reads. Mutation methods
    /// throw, preventing tests from accidentally bypassing the graph model.
    /// </summary>
    private sealed class MutableSnapshotStore : IMeshTopologyStore
    {
        public MutableSnapshotStore(MeshGraphSnapshot snapshot)
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