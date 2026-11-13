using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-06 Commit 1: hand-verified Prim fixtures with exact selected links,
/// parent relationships and simulated total weights. These tests exercise
/// the real PriorityQueue-based algorithm over immutable mesh snapshots.
/// </summary>
public sealed class MeshSpanningTreeServiceTests
{
    [Fact]
    public void GatewayOnlyGraph_ReturnsEmptyZeroCostBackbone()
    {
        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(
                MeshGraphSnapshot.GatewayOnly(42));

        Assert.Equal(42, result.GraphVersion);
        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
        Assert.Empty(result.SelectedLinks);
        Assert.Empty(result.SelectedBranches);
        Assert.Equal(new[] { MeshNodeKey.Gateway }, result.ReachedNodes);
    }

    [Fact]
    public void RegisteredButUnlinkedSensors_AreNotInventedAsReachable()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(8, [Node(a), Node(b)], []);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(8, result.GraphVersion);
        Assert.Empty(result.SelectedBranches);
        Assert.False(result.ContainsNode(a));
        Assert.False(result.ContainsNode(b));
        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
    }

    [Fact]
    public void KnownWeightedGraph_SelectsMinimumTotalCostWithExactParents()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Snapshot(18,
            [Node(c), Node(a), Node(b)],
            [
                Link(6, MeshNodeKey.Gateway, c, 20),
                Link(4, a, c, 10),
                Link(2, MeshNodeKey.Gateway, b, 4),
                Link(5, b, c, 3),
                Link(3, a, b, 2),
                Link(1, MeshNodeKey.Gateway, a, 1)
            ]);

        // Prim selects GW-A (1), A-B (2), B-C (3): total weight 6.
        // More expensive direct links are retained in the source graph but
        // are not part of the minimum spanning backbone.
        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(18, result.GraphVersion);
        Assert.Equal(6d, result.TotalSimulatedEnergyCost);
        Assert.Equal(3, result.SelectedEdgeCount);
        Assert.Equal(4, result.ReachedNodeCount);
        Assert.Equal(new[] { LinkId(1), LinkId(3), LinkId(5) },
            result.SelectedBranches.Select(branch => branch.Link.Id));
        AssertParent(result, a, MeshNodeKey.Gateway);
        AssertParent(result, b, a);
        AssertParent(result, c, b);
    }

    [Fact]
    public void PrimOptimisesBackboneCost_NotFewestHopRoute()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(5,
            [Node(a), Node(b)],
            [
                Link(1, MeshNodeKey.Gateway, a, 100),
                Link(2, MeshNodeKey.Gateway, b, 1),
                Link(3, b, a, 1)
            ]);

        MinimumSpanningTreeResult tree =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);
        MeshGatewayRouting bfs = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(2d, tree.TotalSimulatedEnergyCost);
        AssertParent(tree, a, b); // Tree path uses two links to the gateway.
        AssertParent(tree, b, MeshNodeKey.Gateway);
        Assert.True(bfs.TryGetRouteToGateway(a, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(1, route.HopCount); // BFS instead chooses direct GW-A.
        Assert.Equal(new[] { a, MeshNodeKey.Gateway }, route.PathToGateway);
    }

    [Fact]
    public void EqualCostEdges_UseStableCanonicalIdentityTieBreaking()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Snapshot(11,
            [Node(c), Node(b), Node(a)],
            [
                Link(4, b, c, 1),
                Link(3, a, c, 1),
                Link(2, MeshNodeKey.Gateway, b, 1),
                Link(1, MeshNodeKey.Gateway, a, 1)
            ]);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(3d, result.TotalSimulatedEnergyCost);
        AssertParent(result, a, MeshNodeKey.Gateway);
        AssertParent(result, b, MeshNodeKey.Gateway);
        AssertParent(result, c, a);
        Assert.Equal(new[] { LinkId(1), LinkId(2), LinkId(3) },
            result.SelectedBranches.Select(branch => branch.Link.Id));
    }

    [Fact]
    public void CycleWithExpensiveChords_ProducesExactlyReachedVerticesMinusOneEdges()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshNodeKey d = Sensor(4);
        MeshGraphSnapshot graph = Snapshot(20,
            [Node(a), Node(b), Node(c), Node(d)],
            [
                Link(1, MeshNodeKey.Gateway, a, 1),
                Link(2, a, b, 1),
                Link(3, b, c, 1),
                Link(4, c, d, 1),
                Link(5, a, d, 50),
                Link(6, MeshNodeKey.Gateway, c, 50),
                Link(7, b, d, 50)
            ]);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(4d, result.TotalSimulatedEnergyCost);
        Assert.Equal(result.ReachedNodeCount - 1, result.SelectedEdgeCount);
        Assert.Equal(new[] { LinkId(1), LinkId(2), LinkId(3), LinkId(4) },
            result.SelectedBranches.Select(branch => branch.Link.Id));
        Assert.Equal(4, result.ReachableSensorCount);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public void UnavailableBridge_IsNeverUsedAsAnIntermediateVertex(
        SensorStatus unavailableStatus)
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(14,
            [Node(bridge, unavailableStatus), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge, 1),
                Link(2, bridge, leaf, 1)
            ]);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Empty(result.SelectedBranches);
        Assert.False(result.ContainsNode(bridge));
        Assert.False(result.ContainsNode(leaf));
        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
        Assert.Equal(new[] { LinkId(1), LinkId(2) },
            graph.Links.Select(link => link.Id)); // Original links remain.
    }

    [Fact]
    public void WarningSensor_CanForwardThroughTheWeightedBackbone()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(13,
            [Node(bridge, SensorStatus.Warning), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge, 3),
                Link(2, bridge, leaf, 2)
            ]);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(5d, result.TotalSimulatedEnergyCost);
        Assert.Equal(2, result.ReachableSensorCount);
        AssertParent(result, bridge, MeshNodeKey.Gateway);
        AssertParent(result, leaf, bridge);
    }

    [Fact]
    public void OtherDisconnectedComponents_DoNotEnterGatewayRootedTree()
    {
        MeshNodeKey connected = Sensor(1);
        MeshNodeKey islandOne = Sensor(2);
        MeshNodeKey islandTwo = Sensor(3);
        MeshGraphSnapshot graph = Snapshot(27,
            [Node(connected), Node(islandOne), Node(islandTwo)],
            [
                Link(1, MeshNodeKey.Gateway, connected, 5),
                Link(2, islandOne, islandTwo, 1)
            ]);

        MinimumSpanningTreeResult result =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(5d, result.TotalSimulatedEnergyCost);
        Assert.Equal(LinkId(1), Assert.Single(result.SelectedLinks).Id);
        Assert.True(result.ContainsNode(connected));
        Assert.False(result.ContainsNode(islandOne));
        Assert.False(result.ContainsNode(islandTwo));
    }

    [Fact]
    public void PermutedInputs_ProduceIdenticalWeightsEdgesAndParents()
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

        MinimumSpanningTreeResult first =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(
                Snapshot(31, nodes, links));
        MinimumSpanningTreeResult second =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(
                Snapshot(31, nodes.Reverse(), links.Reverse()));

        Assert.Equal(first.GraphVersion, second.GraphVersion);
        Assert.Equal(first.TotalSimulatedEnergyCost, second.TotalSimulatedEnergyCost);
        Assert.Equal(first.SelectedLinks.Select(link => link.Id),
            second.SelectedLinks.Select(link => link.Id));
        Assert.Equal(first.SelectedBranches.Select(branch => (branch.Parent, branch.Child)),
            second.SelectedBranches.Select(branch => (branch.Parent, branch.Child)));
    }

    [Fact]
    public void PreCancelledTraversal_ThrowsWithoutReturningPartialTree()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(
                MeshGraphSnapshot.GatewayOnly(), cancellation.Token));
    }

    [Fact]
    public async Task AsyncService_ReadsOneSnapshotPerCalculationAndDoesNotCache()
    {
        MeshNodeKey a = Sensor(1);
        var store = new StaticSnapshotStore(Snapshot(1,
            [Node(a)], [Link(1, MeshNodeKey.Gateway, a, 4)]));
        var service = new MeshSpanningTreeService(store);

        MinimumSpanningTreeResult first =
            await service.GetGatewayMinimumSpanningTreeAsync();
        store.Snapshot = Snapshot(2, [Node(a)], []);
        MinimumSpanningTreeResult second =
            await service.GetGatewayMinimumSpanningTreeAsync();

        Assert.Equal(1, first.GraphVersion);
        Assert.Equal(4d, first.TotalSimulatedEnergyCost);
        Assert.Equal(2, second.GraphVersion);
        Assert.Equal(0d, second.TotalSimulatedEnergyCost);
        Assert.Equal(2, store.ReadCount);
    }

    private static void AssertParent(
        MinimumSpanningTreeResult tree, MeshNodeKey child, MeshNodeKey expected)
    {
        Assert.True(tree.TryGetParent(child, out MeshNodeKey parent));
        Assert.Equal(expected, parent);
        Assert.True(tree.TryGetBranch(child, out MeshTreeBranch? branch));
        Assert.NotNull(branch);
        Assert.Equal(child, branch.Child);
        Assert.Equal(expected, branch.Parent);
        Assert.True(branch.Link.Connects(child, expected));
    }

    private static MeshGraphSnapshot Snapshot(
        long version,
        IEnumerable<MeshNode> sensors,
        IEnumerable<MeshLink> links) =>
        new(version, new[] { MeshNode.Gateway }.Concat(sensors), links);

    private static MeshNode Node(
        MeshNodeKey node, SensorStatus status = SensorStatus.Online) =>
        new(node, status);

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:000000000000}"));

    private static Guid LinkId(int ordinal) =>
        Guid.Parse($"10000000-0000-0000-0000-{ordinal:000000000000}");

    private static MeshLink Link(
        int ordinal, MeshNodeKey first, MeshNodeKey second, double cost) =>
        new(LinkId(ordinal), first, second, cost);

    /// <summary>
    /// Fails if a service tries to mutate the graph or read a second source
    /// for one projection. Only GetSnapshotAsync is supported here.
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