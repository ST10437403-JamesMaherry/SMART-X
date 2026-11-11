using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-05 Commit 3: adversarial, hand-verified BFS fixtures over immutable
/// mesh revisions. This class deliberately exercises the production traversal
/// rather than duplicating its predecessor-selection implementation.
/// </summary>
public sealed class MeshBfsAdversarialRegressionTests
{
    [Fact]
    public void OverlappingCycles_VisitEveryEligibleVertexExactlyOnce()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshNodeKey d = Sensor(4);
        MeshNodeKey e = Sensor(5);

        MeshGraphSnapshot graph = Graph(101,
            [Node(a), Node(b), Node(c), Node(d), Node(e)],
            [
                Link(7, d, e),
                Link(3, b, c),
                Link(8, e, b),
                Link(1, a, MeshNodeKey.Gateway),
                Link(5, b, d),
                Link(4, a, c),
                Link(6, c, d),
                Link(2, a, b)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(101, result.GraphVersion);
        Assert.Equal(5, result.ReachableSensorCount);
        Assert.Empty(result.DisconnectedComponents);
        Assert.Empty(result.UnavailableSensors);
        Assert.Equal(6, result.Reachability.ReachableNodes.Count);
        Assert.Equal(6, result.Reachability.ReachableNodes
            .Select(entry => entry.Node).Distinct().Count());
        AssertRoute(result, e, [e, b, a, MeshNodeKey.Gateway]);
        AssertRoute(result, d, [d, b, a, MeshNodeKey.Gateway]);
    }

    [Fact]
    public void LayeredDiamond_TwoEqualHopDecisionsUseAscendingNodeIdentities()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshNodeKey d = Sensor(4);
        MeshNodeKey target = Sensor(5);
        MeshGraphSnapshot graph = Graph(102,
            [Node(target), Node(d), Node(c), Node(b), Node(a)],
            [
                Link(7, d, target, 0.1),
                Link(6, c, target, 500),
                Link(5, b, d, 0.1),
                Link(4, b, c, 0.1),
                Link(3, a, d, 500),
                Link(2, a, c, 500),
                Link(1, b, MeshNodeKey.Gateway, 0.1),
                Link(8, a, MeshNodeKey.Gateway, 500)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        // Both gateway parents have equal hops; BFS chooses A. At the next
        // layer both C and D have equal hops; the lower identity C wins.
        AssertRoute(result, target, [target, c, a, MeshNodeKey.Gateway]);
        Assert.Equal(3, result.Reachability.ReachableNodes
            .Single(entry => entry.Node == target).HopCount);
    }

    [Fact]
    public void UnavailableShortcut_IsSkippedEvenIfItHasTheLowestWeight()
    {
        MeshNodeKey offline = Sensor(1);
        MeshNodeKey bridge = Sensor(2);
        MeshNodeKey second = Sensor(3);
        MeshNodeKey leaf = Sensor(4);
        MeshGraphSnapshot graph = Graph(103,
            [Node(offline, SensorStatus.Offline), Node(bridge), Node(second), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, offline, 0.01),
                Link(2, offline, leaf, 0.01),
                Link(3, MeshNodeKey.Gateway, bridge, 1000),
                Link(4, bridge, second, 1000),
                Link(5, second, leaf, 1000)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(new[] { offline }, result.UnavailableSensors);
        AssertRoute(result, leaf, [leaf, second, bridge, MeshNodeKey.Gateway]);
        AssertRouteStatus(result, offline, MeshRouteStatus.Unavailable);
        Assert.Empty(result.DisconnectedComponents);
    }

    [Fact]
    public void OfflineArticulationVertex_SplitsReachableCycleFromUnreachableIsland()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey articulation = Sensor(3);
        MeshNodeKey d = Sensor(4);
        MeshNodeKey e = Sensor(5);
        MeshGraphSnapshot graph = Graph(104,
            [Node(a), Node(b), Node(articulation, SensorStatus.Offline), Node(d), Node(e)],
            [
                Link(1, MeshNodeKey.Gateway, a),
                Link(2, a, b),
                Link(3, b, articulation),
                Link(4, a, articulation),
                Link(5, articulation, d),
                Link(6, d, e),
                Link(7, e, articulation)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(2, result.ReachableSensorCount);
        Assert.Equal(1, result.UnavailableSensorCount);
        Assert.Equal(2, result.DisconnectedSensorCount);
        Assert.Equal(new[] { d, e }, Assert.Single(result.DisconnectedComponents).Sensors);
        AssertRoute(result, b, [b, a, MeshNodeKey.Gateway]);
        AssertRouteStatus(result, articulation, MeshRouteStatus.Unavailable);
        AssertRouteStatus(result, e, MeshRouteStatus.Disconnected);
    }

    [Fact]
    public void DisconnectedCyclesAndIsolatedVertices_ArePartitionedDeterministically()
    {
        MeshNodeKey[] keys = Enumerable.Range(1, 8).Select(Sensor).ToArray();
        MeshGraphSnapshot graph = Graph(105,
            [
                Node(keys[7], SensorStatus.Fault), Node(keys[6]), Node(keys[5]),
                Node(keys[4]), Node(keys[3]), Node(keys[2]), Node(keys[1]), Node(keys[0])
            ],
            [
                Link(1, MeshNodeKey.Gateway, keys[0]),
                Link(2, keys[1], keys[2]),
                Link(3, keys[2], keys[3]),
                Link(4, keys[3], keys[1]),
                Link(5, keys[4], keys[5])
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Equal(1, result.ReachableSensorCount);
        Assert.Equal(6, result.DisconnectedSensorCount);
        Assert.Equal(new[] { keys[7] }, result.UnavailableSensors);
        Assert.Equal(3, result.DisconnectedComponents.Count);
        Assert.Equal(new[] { keys[1], keys[2], keys[3] },
            result.DisconnectedComponents[0].Sensors);
        Assert.Equal(new[] { keys[4], keys[5] },
            result.DisconnectedComponents[1].Sensors);
        Assert.Equal(new[] { keys[6] },
            result.DisconnectedComponents[2].Sensors);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public void NonForwardingBridge_HasNoRouteAndCannotRelay(SensorStatus status)
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey target = Sensor(2);
        MeshGraphSnapshot graph = Graph(106,
            [Node(bridge, status), Node(target)],
            [
                Link(1, MeshNodeKey.Gateway, bridge),
                Link(2, bridge, target)
            ]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        AssertRouteStatus(result, bridge, MeshRouteStatus.Unavailable);
        AssertRouteStatus(result, target, MeshRouteStatus.Disconnected);
        Assert.Equal(new[] { target }, Assert.Single(result.DisconnectedComponents).Sensors);
        Assert.Single(result.Reachability.ReachableNodes);
    }

    [Fact]
    public void WarningBridge_IsEligibleAndPreservesDownstreamRoute()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Graph(107,
            [Node(bridge, SensorStatus.Warning), Node(leaf)],
            [Link(1, MeshNodeKey.Gateway, bridge), Link(2, bridge, leaf)]);

        AssertRoute(MeshTraversalService.CalculateGatewayRouting(graph), leaf,
            [leaf, bridge, MeshNodeKey.Gateway]);
    }

    [Fact]
    public void AllSensorsUnavailable_LeavesOnlyTheGatewayReachable()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Graph(108,
            [
                Node(a, SensorStatus.Offline),
                Node(b, SensorStatus.Fault),
                Node(c, SensorStatus.Maintenance)
            ],
            [Link(1, MeshNodeKey.Gateway, a), Link(2, a, b), Link(3, b, c)]);

        MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(graph);

        Assert.Single(result.Reachability.ReachableNodes);
        Assert.Equal(new[] { a, b, c }, result.UnavailableSensors);
        Assert.Empty(result.DisconnectedComponents);
        Assert.Equal(0, result.DisconnectedSensorCount);
        Assert.Equal(0, result.ReachableSensorCount);
    }

    [Fact]
    public void AlternateRoute_ReconstructsAfterFailedBridgeWithoutInventingNewLink()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey target = Sensor(3);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, a),
            Link(2, MeshNodeKey.Gateway, b),
            Link(3, a, target),
            Link(4, b, target)
        ];
        MeshGatewayRouting before = MeshTraversalService.CalculateGatewayRouting(
            Graph(109, [Node(a), Node(b), Node(target)], links));
        MeshGatewayRouting after = MeshTraversalService.CalculateGatewayRouting(
            Graph(110, [Node(a, SensorStatus.Offline), Node(b), Node(target)], links));

        AssertRoute(before, target, [target, a, MeshNodeKey.Gateway]);
        AssertRoute(after, target, [target, b, MeshNodeKey.Gateway]);
        AssertRouteStatus(after, a, MeshRouteStatus.Unavailable);
        Assert.Equal(109, before.GraphVersion);
        Assert.Equal(110, after.GraphVersion);
        Assert.Equal(4, links.Length); // The configured graph was not changed.
    }

    [Fact]
    public void WithoutAlternateLink_BridgeFailureDoesNotClaimSelfHealing()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey downstream = Sensor(2);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, bridge),
            Link(2, bridge, downstream)
        ];
        MeshGatewayRouting before = MeshTraversalService.CalculateGatewayRouting(
            Graph(111, [Node(bridge), Node(downstream)], links));
        MeshGatewayRouting after = MeshTraversalService.CalculateGatewayRouting(
            Graph(112, [Node(bridge, SensorStatus.Fault), Node(downstream)], links));

        AssertRoute(before, downstream, [downstream, bridge, MeshNodeKey.Gateway]);
        AssertRouteStatus(after, downstream, MeshRouteStatus.Disconnected);
        AssertRouteStatus(after, bridge, MeshRouteStatus.Unavailable);
    }

    [Fact]
    public void ReversedEdgeOrientationAndNodeInputOrder_CannotChangeTieBreak()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey target = Sensor(3);
        MeshNode[] sensors = [Node(a), Node(b), Node(target)];
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, a),
            Link(2, MeshNodeKey.Gateway, b),
            Link(3, a, target),
            Link(4, b, target)
        ];
        MeshGraphSnapshot ordered = Graph(113, sensors, links);
        MeshGraphSnapshot reversed = Graph(113, sensors.Reverse().ToArray(),
            links.Reverse().Select(link =>
                new MeshLink(link.Id, link.NodeB, link.NodeA,
                    link.SimulatedEnergyCost)).ToArray());

        MeshGatewayRouting left = MeshTraversalService.CalculateGatewayRouting(ordered);
        MeshGatewayRouting right = MeshTraversalService.CalculateGatewayRouting(reversed);

        foreach (MeshNodeKey sensor in new[] { a, b, target })
        {
            Assert.True(left.TryGetRouteToGateway(sensor, out MeshRoute? first));
            Assert.True(right.TryGetRouteToGateway(sensor, out MeshRoute? second));
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first.PathToGateway.ToArray(), second.PathToGateway.ToArray());
        }
        AssertRoute(right, target, [target, a, MeshNodeKey.Gateway]);
    }

    [Fact]
    public void DuplicateUnorderedPair_IsRejectedBeforeTraversalCanDoubleCountIt()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshLink first = Link(1, a, b);
        MeshLink mirroredWithNewId = Link(2, b, a);

        Assert.Throws<InvalidMeshTopologyException>(() => Graph(114,
            [Node(a), Node(b)], [first, mirroredWithNewId]));
    }

    [Fact]
    public void RouteResultsRemainImmutableAcrossLaterRevisions()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGatewayRouting original = MeshTraversalService.CalculateGatewayRouting(
            Graph(115, [Node(bridge), Node(leaf)],
                [Link(1, MeshNodeKey.Gateway, bridge), Link(2, bridge, leaf)]));
        Assert.True(original.TryGetRouteToGateway(leaf, out MeshRoute? saved));
        Assert.NotNull(saved);

        MeshGatewayRouting changed = MeshTraversalService.CalculateGatewayRouting(
            Graph(116, [Node(bridge, SensorStatus.Offline), Node(leaf)],
                [Link(1, MeshNodeKey.Gateway, bridge), Link(2, bridge, leaf)]));

        AssertRouteStatus(changed, leaf, MeshRouteStatus.Disconnected);
        Assert.Equal(115, saved.GraphVersion);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, saved.PathToGateway);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)saved.PathToGateway).Clear());
        Assert.Equal(115, original.GraphVersion);
    }

    [Fact]
    public void LongChain_HasExactHopCountAndFiniteSimplePath()
    {
        const int sensorCount = 128;
        MeshNodeKey[] sensors = Enumerable.Range(1, sensorCount)
            .Select(Sensor).ToArray();
        var links = new List<MeshLink>(sensorCount)
        {
            Link(1, MeshNodeKey.Gateway, sensors[0])
        };

        for (int index = 1; index < sensorCount; index++)
        {
            links.Add(Link(index + 1, sensors[index - 1], sensors[index]));
        }

        MeshGatewayRouting routing = MeshTraversalService.CalculateGatewayRouting(
            Graph(117, sensors.Select(sensor => Node(sensor)).ToArray(), links));

        Assert.Equal(sensorCount, routing.ReachableSensorCount);
        Assert.True(routing.TryGetRouteToGateway(sensors[^1], out MeshRoute? last));
        Assert.NotNull(last);
        Assert.Equal(sensorCount, last.HopCount);
        Assert.Equal(sensorCount + 1, last.PathToGateway.Count);
        Assert.Equal(sensors[^1], last.PathToGateway[0]);
        Assert.Equal(MeshNodeKey.Gateway, last.PathToGateway[^1]);
        Assert.Equal(last.PathToGateway.Count, last.PathToGateway.Distinct().Count());
    }

    [Fact]
    public void ChangingOnlyEnergyWeights_DoesNotChangeBfsRouteSelection()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey target = Sensor(3);
        MeshLink[] cheapA =
        [
            Link(1, MeshNodeKey.Gateway, a, 0.01),
            Link(2, MeshNodeKey.Gateway, b, 2000),
            Link(3, a, target, 0.01),
            Link(4, b, target, 2000)
        ];
        MeshLink[] cheapB =
        [
            Link(1, MeshNodeKey.Gateway, a, 2000),
            Link(2, MeshNodeKey.Gateway, b, 0.01),
            Link(3, a, target, 2000),
            Link(4, b, target, 0.01)
        ];
        MeshGatewayRouting first = MeshTraversalService.CalculateGatewayRouting(
            Graph(118, [Node(a), Node(b), Node(target)], cheapA));
        MeshGatewayRouting second = MeshTraversalService.CalculateGatewayRouting(
            Graph(119, [Node(a), Node(b), Node(target)], cheapB));

        AssertRoute(first, target, [target, a, MeshNodeKey.Gateway]);
        AssertRoute(second, target, [target, a, MeshNodeKey.Gateway]);
        Assert.Equal(118, first.GraphVersion);
        Assert.Equal(119, second.GraphVersion);
    }

    [Fact]
    public void FixedSeedGraphs_MatchIndependentEdgeRelaxationOracle()
    {
        // Independent repeated relaxation, rather than duplicating BFS, is
        // the oracle for unweighted shortest-hop distances on small graphs.
        // The seed makes every generated topology fully reproducible.
        var random = new Random(73120503);
        const int trials = 24;
        const int sensorCount = 8;
        MeshNodeKey[] keys = Enumerable.Range(1, sensorCount).Select(Sensor).ToArray();

        for (int trial = 0; trial < trials; trial++)
        {
            SensorStatus[] statuses = Enumerable.Range(0, sensorCount)
                .Select(_ => random.Next(5) switch
                {
                    0 => SensorStatus.Offline,
                    1 => SensorStatus.Warning,
                    2 => SensorStatus.Fault,
                    3 => SensorStatus.Maintenance,
                    _ => SensorStatus.Online
                }).ToArray();
            MeshNode[] nodes = keys.Select((key, index) =>
                Node(key, statuses[index])).ToArray();
            MeshNodeKey[] everyKey = [MeshNodeKey.Gateway, .. keys];
            var links = new List<MeshLink>();
            int linkOrdinal = 1;

            for (int first = 0; first < everyKey.Length; first++)
            {
                for (int second = first + 1; second < everyKey.Length; second++)
                {
                    if (random.Next(4) == 0)
                    {
                        links.Add(Link(linkOrdinal++, everyKey[first],
                            everyKey[second], random.Next(1, 500)));
                    }
                }
            }

            MeshGraphSnapshot snapshot = Graph(200 + trial, nodes, links);
            MeshGatewayRouting result = MeshTraversalService.CalculateGatewayRouting(snapshot);
            Dictionary<MeshNodeKey, int> expected = CalculateHopCountsByRelaxation(snapshot);
            var linkPairs = snapshot.Links
                .Select(link => (link.NodeA, link.NodeB)).ToHashSet();

            foreach (MeshNode node in snapshot.Nodes.Where(node => !node.IsGateway))
            {
                Assert.True(result.TryGetRouteToGateway(node.Key, out MeshRoute? route));
                Assert.NotNull(route);
                Assert.Equal(snapshot.Version, route.GraphVersion);

                if (!node.IsForwardingEligible)
                {
                    Assert.Equal(MeshRouteStatus.Unavailable, route.Status);
                    Assert.Null(route.HopCount);
                    Assert.Empty(route.PathToGateway);
                    continue;
                }

                int expectedHops = expected[node.Key];
                if (expectedHops == int.MaxValue)
                {
                    Assert.Equal(MeshRouteStatus.Disconnected, route.Status);
                    Assert.Null(route.HopCount);
                    Assert.Empty(route.PathToGateway);
                    continue;
                }

                Assert.Equal(MeshRouteStatus.Reachable, route.Status);
                Assert.Equal(expectedHops, route.HopCount);
                Assert.Equal(expectedHops + 1, route.PathToGateway.Count);
                Assert.Equal(node.Key, route.PathToGateway[0]);
                Assert.Equal(MeshNodeKey.Gateway, route.PathToGateway[^1]);
                Assert.Equal(route.PathToGateway.Count,
                    route.PathToGateway.Distinct().Count());

                for (int index = 0; index < route.PathToGateway.Count - 1; index++)
                {
                    MeshNodeKey first = route.PathToGateway[index];
                    MeshNodeKey second = route.PathToGateway[index + 1];
                    var pair = first.CompareTo(second) < 0
                        ? (first, second)
                        : (second, first);
                    Assert.Contains(pair, linkPairs);
                    Assert.Equal(expected[first] - 1, expected[second]);
                }
            }
        }
    }

    private static Dictionary<MeshNodeKey, int> CalculateHopCountsByRelaxation(
        MeshGraphSnapshot snapshot)
    {
        var distances = snapshot.Nodes.ToDictionary(node => node.Key,
            node => node.IsGateway ? 0 : int.MaxValue);
        var eligible = snapshot.Nodes.Where(node => node.IsForwardingEligible)
            .Select(node => node.Key).ToHashSet();

        // At most V-1 passes are needed for all finite shortest paths. This
        // independent reference is deliberately NOT the production queue BFS.
        for (int pass = 1; pass < snapshot.Nodes.Count; pass++)
        {
            bool changed = false;
            foreach (MeshLink link in snapshot.Links)
            {
                if (!eligible.Contains(link.NodeA) || !eligible.Contains(link.NodeB))
                {
                    continue;
                }

                int a = distances[link.NodeA];
                int b = distances[link.NodeB];
                if (a != int.MaxValue && a + 1 < b)
                {
                    distances[link.NodeB] = a + 1;
                    changed = true;
                }
                if (b != int.MaxValue && b + 1 < distances[link.NodeA])
                {
                    distances[link.NodeA] = b + 1;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return distances;
    }

    private static void AssertRoute(
        MeshGatewayRouting routing,
        MeshNodeKey sensor,
        MeshNodeKey[] expected)
    {
        Assert.True(routing.TryGetRouteToGateway(sensor, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(expected, route.PathToGateway);
        Assert.Equal(expected.Length - 1, route.HopCount);
        Assert.Equal(routing.GraphVersion, route.GraphVersion);
    }

    private static void AssertRouteStatus(
        MeshGatewayRouting routing,
        MeshNodeKey sensor,
        MeshRouteStatus expected)
    {
        Assert.True(routing.TryGetRouteToGateway(sensor, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(expected, route.Status);
        Assert.Null(route.HopCount);
        Assert.Empty(route.PathToGateway);
    }

    private static MeshNodeKey Sensor(int ordinal) => MeshNodeKey.ForSensor(
        Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}"));

    private static MeshNode Node(
        MeshNodeKey sensor,
        SensorStatus status = SensorStatus.Online) => new(sensor, status);

    private static MeshLink Link(
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double energyCost = 1) =>
        new(Guid.Parse($"10000000-0000-0000-0000-{ordinal:D12}"),
            first, second, energyCost);

    private static MeshGraphSnapshot Graph(
        long version,
        IReadOnlyList<MeshNode> sensors,
        IReadOnlyList<MeshLink> links) =>
        new(version, [MeshNode.Gateway, .. sensors], links);
}