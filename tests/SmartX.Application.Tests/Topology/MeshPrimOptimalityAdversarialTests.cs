using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-06 Commit 4: independent, deterministic evidence that gateway-rooted
/// Prim chooses a minimum-total-cost tree rather than merely a plausible
/// set of routes. The oracle uses Kruskal with disjoint sets, NOT Prim's heap.
///
/// All weights are relative simulated costs. Neither this fixture nor the
/// production algorithm claims a shortest weighted path to every sensor.
/// </summary>
public sealed class MeshPrimOptimalityAdversarialTests
{
    [Theory]
    [InlineData(0)] // Unique integer weights.
    [InlineData(1)] // Every edge has the same weight.
    [InlineData(2)] // Repeated fractional weights and ties.
    public void AllFourVertexUndirectedGraphs_AgreeWithIndependentKruskal(int costProfile)
    {
        MeshNodeKey[] vertices = Vertices(4); // Gateway and three sensors.
        (MeshNodeKey From, MeshNodeKey To)[] pairs = AllPairs(vertices);
        Assert.Equal(6, pairs.Length);

        // Exactly 2^6 possible simple undirected edge sets, including an
        // empty graph and every disconnected configuration. A single xUnit
        // theory case checks each mask without relying on a random seed.
        for (int mask = 0; mask < (1 << pairs.Length); mask++)
        {
            MeshGraphSnapshot graph = GraphFromMask(
                1000 + (costProfile * 64) + mask,
                vertices, pairs, mask, costProfile);

            VerifyOptimalityAgainstKruskal(graph);
        }
    }

    [Fact]
    public void AllFiveVertexEdgeSets_AgreeWithIndependentKruskalAndCutProperty()
    {
        MeshNodeKey[] vertices = Vertices(5); // Gateway and four sensors.
        (MeshNodeKey From, MeshNodeKey To)[] pairs = AllPairs(vertices);
        Assert.Equal(10, pairs.Length);

        // Exhaustively exercise all 1,024 subsets, with nonuniform weights.
        // This cross-check proves minimum total cost, tree size, connectivity,
        // absence of cycles and the MST cycle/cut exchange property.
        for (int mask = 0; mask < (1 << pairs.Length); mask++)
        {
            MeshGraphSnapshot graph = GraphFromMask(
                2000 + mask,
                vertices, pairs, mask, 2);

            VerifyOptimalityAgainstKruskal(graph);
        }
    }

    [Fact]
    public void FullyConnectedEqualWeightGraph_UsesCanonicalGatewayStar()
    {
        MeshNodeKey[] vertices = Vertices(5);
        (MeshNodeKey From, MeshNodeKey To)[] pairs = AllPairs(vertices);
        MeshGraphSnapshot graph = GraphFromMask(
            3200, vertices, pairs, (1 << pairs.Length) - 1, 1);

        MinimumSpanningTreeResult tree =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);

        Assert.Equal(5, tree.ReachedNodeCount);
        Assert.Equal(4, tree.SelectedEdgeCount);
        Assert.Equal(4d, tree.TotalSimulatedEnergyCost);
        Assert.All(tree.SelectedBranches, branch =>
            Assert.Equal(MeshNodeKey.Gateway, branch.Parent));
        Assert.Equal(vertices.Skip(1),
            tree.SelectedBranches.Select(branch => branch.Child));
        VerifyOptimalityAgainstKruskal(graph);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public void UnavailablePreferredRelay_IsExcludedWithoutRemovingConfiguredEdges(
        SensorStatus unavailableStatus)
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey leaf = Sensor(3);
        MeshLink[] links =
        [
            Link(21, MeshNodeKey.Gateway, a, 1),
            Link(22, a, leaf, 1),
            Link(23, MeshNodeKey.Gateway, b, 5),
            Link(24, b, leaf, 1)
        ];
        MeshGraphSnapshot graph = new(41,
            [MeshNode.Gateway, new MeshNode(a, unavailableStatus),
                new MeshNode(b, SensorStatus.Online),
                new MeshNode(leaf, SensorStatus.Online)],
            links);

        VerifyOptimalityAgainstKruskal(graph);
        MeshGatewaySpanningTreeRouting routing =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(6d, routing.TotalSimulatedEnergyCost);
        Assert.Equal(2, routing.ReachableSensorCount);
        Assert.Equal(new[] { a }, routing.UnavailableSensors);
        Assert.Equal(4, graph.Links.Count);
        AssertRoute(routing, leaf,
            [leaf, b, MeshNodeKey.Gateway], 6d);
        Assert.True(routing.TryGetTreeRouteToGateway(a, out MeshTreeRoute? unavailable));
        Assert.NotNull(unavailable);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.PathToGateway);
    }

    [Fact]
    public void WarningRelay_RemainsEligibleForMinimumCostTree()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = new(51,
            [MeshNode.Gateway, new MeshNode(bridge, SensorStatus.Warning),
                new MeshNode(leaf, SensorStatus.Online)],
            [Link(31, MeshNodeKey.Gateway, bridge, 2),
                Link(32, bridge, leaf, 3)]);

        VerifyOptimalityAgainstKruskal(graph);
        MeshGatewaySpanningTreeRouting routing =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);
        Assert.Equal(5d, routing.TotalSimulatedEnergyCost);
        AssertRoute(routing, leaf,
            [leaf, bridge, MeshNodeKey.Gateway], 5d);
    }

    [Fact]
    public void DisconnectedIslands_NeverGetInventedGatewayPaths()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshNodeKey isolated = Sensor(4);
        MeshGraphSnapshot graph = new(66,
            [MeshNode.Gateway, Online(a), Online(b), Online(c), Online(isolated)],
            [Link(41, MeshNodeKey.Gateway, a, 2), Link(42, b, c, 0.5)]);

        VerifyOptimalityAgainstKruskal(graph);
        MeshGatewaySpanningTreeRouting routing =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        Assert.Equal(2d, routing.TotalSimulatedEnergyCost);
        Assert.Equal(1, routing.ReachableSensorCount);
        Assert.Equal(3, routing.DisconnectedSensorCount);
        Assert.Equal(2, routing.DisconnectedComponents.Count);
        AssertRoute(routing, a, [a, MeshNodeKey.Gateway], 2d);
        foreach (MeshNodeKey sensor in new[] { b, c, isolated })
        {
            Assert.True(routing.TryGetTreeRouteToGateway(sensor,
                out MeshTreeRoute? route));
            Assert.NotNull(route);
            Assert.Equal(MeshRouteStatus.Disconnected, route.Status);
            Assert.Empty(route.PathToGateway);
            Assert.Null(route.AccumulatedSimulatedEnergyCost);
        }
    }

    [Fact]
    public void MstMinimizesBackboneCost_ButMayUseCostlierIndividualDevicePath()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot graph = new(91,
            [MeshNode.Gateway, Online(a), Online(b)],
            [Link(51, MeshNodeKey.Gateway, a, 1),
                Link(52, MeshNodeKey.Gateway, b, 1.5),
                Link(53, a, b, 1)]);

        VerifyOptimalityAgainstKruskal(graph);
        MeshGatewaySpanningTreeRouting routing =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(graph);

        // Prim selects GW--A (1) and A--B (1), total 2. B's chosen
        // tree path costs 2, even though the excluded direct GW--B radio
        // link costs only 1.5. An MST is NOT a shortest-weighted-path tree.
        Assert.Equal(2d, routing.TotalSimulatedEnergyCost);
        AssertRoute(routing, b, [b, a, MeshNodeKey.Gateway], 2d);
        Assert.Equal(2, routing.SelectedEdgeCount);
        Assert.Contains(graph.Links, link =>
            link.Connects(MeshNodeKey.Gateway, b) &&
            link.SimulatedEnergyCost == 1.5d);

        // BFS solves a third objective: fewest hops. It returns the direct
        // single-hop path to B, not the selected two-hop MST path.
        MeshGatewayRouting bfs =
            MeshTraversalService.CalculateGatewayRouting(graph);
        Assert.True(bfs.TryGetRouteToGateway(b, out MeshRoute? shortestHop));
        Assert.NotNull(shortestHop);
        Assert.Equal(1, shortestHop.HopCount);
        Assert.Equal(new[] { b, MeshNodeKey.Gateway }, shortestHop.PathToGateway);
    }

    [Fact]
    public void CancelledOptimalityCalculation_RejectsWorkWithoutPublishingTree()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(
                MeshGraphSnapshot.GatewayOnly(100), cancellation.Token));
    }

    /// <summary>
    /// Independent minimum-cost oracle. Kruskal sorts ALL eligible edges
    /// globally and joins different disjoint sets; Prim, by contrast, grows
    /// one gateway-rooted frontier in a priority heap. We compare the cost
    /// for the gateway component, not the forest cost of disconnected islands.
    /// </summary>
    private static void VerifyOptimalityAgainstKruskal(MeshGraphSnapshot graph)
    {
        MinimumSpanningTreeResult actual =
            MeshSpanningTreeService.CalculateGatewayMinimumSpanningTree(graph);
        MeshNodeKey[] eligible = graph.Nodes
            .Where(node => node.IsForwardingEligible)
            .Select(node => node.Key)
            .ToArray();
        var eligibleSet = eligible.ToHashSet();
        MeshLink[] eligibleLinks = graph.Links
            .Where(link => eligibleSet.Contains(link.NodeA) &&
                eligibleSet.Contains(link.NodeB))
            .ToArray();

        // Connectivity is determined independently of MST edge selection.
        var connectivity = new DisjointSet(eligible);
        foreach (MeshLink link in eligibleLinks)
        {
            connectivity.Union(link.NodeA, link.NodeB);
        }

        MeshNodeKey gatewayRoot = connectivity.Find(MeshNodeKey.Gateway);
        MeshNodeKey[] expectedReached = eligible
            .Where(key => connectivity.Find(key) == gatewayRoot)
            .OrderBy(key => key)
            .ToArray();
        var expectedReachedSet = expectedReached.ToHashSet();

        // An independent Kruskal forest gives the minimum total weight in
        // every eligible connected component. Only its gateway component
        // contributes to a gateway-rooted Prim result.
        var kruskal = new DisjointSet(eligible);
        var forest = new List<MeshLink>();
        foreach (MeshLink link in eligibleLinks
            .OrderBy(link => link.SimulatedEnergyCost)
            .ThenBy(link => link.NodeA)
            .ThenBy(link => link.NodeB)
            .ThenBy(link => link.Id))
        {
            if (kruskal.Union(link.NodeA, link.NodeB))
            {
                forest.Add(link);
            }
        }

        double expectedCost = forest
            .Where(link => expectedReachedSet.Contains(link.NodeA) &&
                expectedReachedSet.Contains(link.NodeB))
            .Sum(link => link.SimulatedEnergyCost);

        Assert.Equal(graph.Version, actual.GraphVersion);
        Assert.Equal(expectedReached, actual.ReachedNodes);
        Assert.Equal(expectedReached.Length - 1, actual.SelectedEdgeCount);
        Assert.Equal(expectedCost, actual.TotalSimulatedEnergyCost);

        // Validate the actual tree independently: every selected branch
        // must be a configured eligible link and must not close a cycle.
        var cycleDetector = new DisjointSet(expectedReached);
        double selectedCost = 0d;
        foreach (MeshTreeBranch branch in actual.SelectedBranches)
        {
            Assert.Contains(branch.Parent, expectedReached);
            Assert.Contains(branch.Child, expectedReached);
            Assert.Contains(graph.Links, configured =>
                configured.Id == branch.Link.Id &&
                configured.NodeA == branch.Link.NodeA &&
                configured.NodeB == branch.Link.NodeB &&
                configured.SimulatedEnergyCost == branch.Link.SimulatedEnergyCost);
            Assert.True(cycleDetector.Union(branch.Parent, branch.Child));
            selectedCost += branch.Link.SimulatedEnergyCost;
        }

        Assert.Equal(expectedCost, selectedCost);
        Assert.Equal(actual.SelectedEdgeCount, actual.SelectedLinks.Count);

        // MST exchange certificate: a non-tree edge cannot be cheaper than
        // the most expensive edge on its unique path inside the selected
        // tree. Otherwise swapping that edge would strictly reduce the cost.
        var selectedIds = actual.SelectedLinks.Select(link => link.Id).ToHashSet();
        foreach (MeshLink excluded in eligibleLinks)
        {
            if (selectedIds.Contains(excluded.Id) ||
                !expectedReachedSet.Contains(excluded.NodeA) ||
                !expectedReachedSet.Contains(excluded.NodeB))
            {
                continue;
            }

            double maximumOnPath = MaximumTreePathCost(
                actual.SelectedLinks, excluded.NodeA, excluded.NodeB);
            Assert.True(excluded.SimulatedEnergyCost >= maximumOnPath,
                $"Link {excluded.Id} undercuts the selected tree path: " +
                $"{excluded.SimulatedEnergyCost} < {maximumOnPath}.");
        }
    }

    private static double MaximumTreePathCost(
        IReadOnlyList<MeshLink> treeLinks,
        MeshNodeKey from,
        MeshNodeKey to)
    {
        var seen = new HashSet<MeshNodeKey> { from };
        var queue = new Queue<(MeshNodeKey Node, double Maximum)>();
        queue.Enqueue((from, 0d));

        // Deliberately independent of Prim's parent dictionary: search the
        // public selected-edge list as an undirected tree.
        while (queue.Count > 0)
        {
            (MeshNodeKey current, double maximum) = queue.Dequeue();
            if (current == to)
            {
                return maximum;
            }

            foreach (MeshLink edge in treeLinks)
            {
                if (!edge.Contains(current))
                {
                    continue;
                }

                MeshNodeKey neighbor = edge.OtherEndpoint(current);
                if (seen.Add(neighbor))
                {
                    queue.Enqueue((neighbor,
                        Math.Max(maximum, edge.SimulatedEnergyCost)));
                }
            }
        }

        throw new InvalidOperationException(
            "A selected MST is missing a path between reached vertices.");
    }

    private static MeshGraphSnapshot GraphFromMask(
        long version,
        MeshNodeKey[] vertices,
        (MeshNodeKey From, MeshNodeKey To)[] pairs,
        int mask,
        int costProfile)
    {
        var links = new List<MeshLink>();
        for (int index = 0; index < pairs.Length; index++)
        {
            if ((mask & (1 << index)) == 0)
            {
                continue;
            }

            double cost = costProfile switch
            {
                0 => index + 1d,
                1 => 1d,
                2 => (index % 4 + 1) * 0.5d,
                _ => throw new ArgumentOutOfRangeException(nameof(costProfile))
            };
            links.Add(Link(index + 1, pairs[index].From, pairs[index].To, cost));
        }

        return new MeshGraphSnapshot(version,
            new[] { MeshNode.Gateway }
                .Concat(vertices.Skip(1).Select(Online)),
            links);
    }

    private static MeshNodeKey[] Vertices(int count) =>
        new[] { MeshNodeKey.Gateway }
            .Concat(Enumerable.Range(1, count - 1).Select(Sensor))
            .ToArray();

    private static (MeshNodeKey From, MeshNodeKey To)[] AllPairs(
        MeshNodeKey[] vertices)
    {
        var pairs = new List<(MeshNodeKey, MeshNodeKey)>();
        for (int first = 0; first < vertices.Length; first++)
        {
            for (int second = first + 1; second < vertices.Length; second++)
            {
                pairs.Add((vertices[first], vertices[second]));
            }
        }

        return pairs.ToArray();
    }

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:X12}"));

    private static MeshNode Online(MeshNodeKey sensor) =>
        new(sensor, SensorStatus.Online);

    private static MeshLink Link(
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double cost) =>
        new(Guid.Parse($"30000000-0000-0000-0000-{ordinal:X12}"),
            first, second, cost);

    private static void AssertRoute(
        MeshGatewaySpanningTreeRouting routing,
        MeshNodeKey sensor,
        MeshNodeKey[] expectedPath,
        double expectedCost)
    {
        Assert.True(routing.TryGetTreeRouteToGateway(sensor,
            out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(routing.GraphVersion, route.GraphVersion);
        Assert.Equal(expectedPath, route.PathToGateway);
        Assert.Equal(expectedPath.Length - 1, route.HopCount);
        Assert.Equal(expectedCost, route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(expectedCost,
            route.SelectedLinksToGateway.Sum(link => link.SimulatedEnergyCost));
        foreach (MeshLink selected in route.SelectedLinksToGateway)
        {
            Assert.Contains(routing.Tree.SelectedLinks,
                link => link.Id == selected.Id);
        }
    }

    /// <summary>
    /// Tiny independent union-find implementation for the Kruskal oracle.
    /// It is test-only and never replaces production graph operations.
    /// </summary>
    private sealed class DisjointSet
    {
        private readonly Dictionary<MeshNodeKey, MeshNodeKey> _parent;
        private readonly Dictionary<MeshNodeKey, int> _rank;

        public DisjointSet(IEnumerable<MeshNodeKey> nodes)
        {
            MeshNodeKey[] copy = nodes.ToArray();
            _parent = copy.ToDictionary(node => node, node => node);
            _rank = copy.ToDictionary(node => node, _ => 0);
        }

        public MeshNodeKey Find(MeshNodeKey node)
        {
            MeshNodeKey root = node;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            // Path compression is unrelated to the MST algorithm under test.
            while (_parent[node] != node)
            {
                MeshNodeKey next = _parent[node];
                _parent[node] = root;
                node = next;
            }

            return root;
        }

        public bool Union(MeshNodeKey first, MeshNodeKey second)
        {
            MeshNodeKey a = Find(first);
            MeshNodeKey b = Find(second);
            if (a == b)
            {
                return false;
            }

            if (_rank[a] < _rank[b])
            {
                (a, b) = (b, a);
            }

            _parent[b] = a;
            if (_rank[a] == _rank[b])
            {
                _rank[a]++;
            }

            return true;
        }
    }
}