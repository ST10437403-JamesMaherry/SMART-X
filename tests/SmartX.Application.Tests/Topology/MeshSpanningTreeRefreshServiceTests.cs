using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-06 Commit 3: deterministic revision-to-revision Prim recalculation.
/// Every fixture uses real configured edges only, and each revision contains
/// its own sensor health. Commit 4 will add independent MST optimality proofs.
/// </summary>
public sealed class MeshSpanningTreeRefreshServiceTests
{
    [Fact]
    public void OfflinePreferredBridge_ChangesLeafToRealRedundantRoute()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey leaf = Sensor(3);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, a, leaf, 1),
            Link(3, MeshNodeKey.Gateway, b, 2),
            Link(4, b, leaf, 3)
        ];
        MeshGraphSnapshot before = Graph(14,
            [Node(a), Node(b), Node(leaf)], links);
        MeshGraphSnapshot failed = Graph(15,
            [Node(a, SensorStatus.Offline), Node(b), Node(leaf)], links);

        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(before);
        MeshSpanningTreeRefresh refresh =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, failed);

        Assert.Equal(4d, original.TotalSimulatedEnergyCost);
        Assert.Equal(5d, refresh.Current.TotalSimulatedEnergyCost);
        Assert.Equal(new[] { leaf }, refresh.ReroutedSensors);
        Assert.Equal(new[] { a }, refresh.LostGatewayConnectivitySensors);
        Assert.True(refresh.Current.TryGetTreeRouteToGateway(leaf, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(new[] { leaf, b, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Equal(5d, route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(4, failed.Links.Count); // Offline did not delete links.
        Assert.Equal(14, original.GraphVersion); // Old projection is immutable.
        Assert.Equal(15, route.GraphVersion);
    }

    [Fact]
    public void NoRedundantBridge_FailureExplicitlyDisconnectsDownstream()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshLink[] links =
        [
            Link(1, MeshNodeKey.Gateway, bridge, 1),
            Link(2, bridge, leaf, 1)
        ];
        MeshGraphSnapshot before = Graph(10, [Node(bridge), Node(leaf)], links);
        MeshGraphSnapshot failed = Graph(11,
            [Node(bridge, SensorStatus.Fault), Node(leaf)], links);
        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(before);

        MeshSpanningTreeRefresh refresh =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, failed);

        Assert.Equal(0d, refresh.Current.TotalSimulatedEnergyCost);
        Assert.Empty(refresh.Current.Tree.SelectedLinks);
        Assert.Equal(new[] { bridge, leaf }, refresh.LostGatewayConnectivitySensors);
        Assert.True(refresh.Current.TryGetTreeRouteToGateway(bridge, out MeshTreeRoute? unavailable));
        Assert.True(refresh.Current.TryGetTreeRouteToGateway(leaf, out MeshTreeRoute? disconnected));
        Assert.NotNull(unavailable);
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);
        Assert.Null(disconnected.AccumulatedSimulatedEnergyCost);
        Assert.Empty(disconnected.PathToGateway);
        Assert.Equal(2, failed.Links.Count); // No artificial failover link.
    }

    [Fact]
    public void LoweringRealLinkCost_RecomputesTreeAndUpdatesPath()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot before = Graph(23, [Node(a), Node(b)],
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, a, b, 1),
            Link(3, MeshNodeKey.Gateway, b, 5)
        ]);
        MeshGraphSnapshot after = Graph(24, [Node(a), Node(b)],
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, a, b, 1),
            Link(3, MeshNodeKey.Gateway, b, 0.5)
        ]);
        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(before);

        MeshSpanningTreeRefresh result =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, after);

        Assert.True(result.HasBackboneChanged);
        Assert.Equal(2d, original.TotalSimulatedEnergyCost);
        Assert.Equal(1.5d, result.Current.TotalSimulatedEnergyCost);
        Assert.Equal(new[] { b }, result.ReroutedSensors);
        MeshTreeRouteTransition change = Assert.Single(result.RouteTransitions);
        Assert.True(change.WasRerouted);
        Assert.True(change.HasCostChange);
        Assert.Equal(new[] { b, a, MeshNodeKey.Gateway },
            change.PreviousRoute?.PathToGateway);
        Assert.Equal(new[] { b, MeshNodeKey.Gateway },
            change.CurrentRoute?.PathToGateway);
        Assert.Equal(0.5d, change.CurrentRoute?.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public void MutatingOnlyUnusedLink_AdvancesRevisionWithoutChangingBackbone()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot before = Graph(7, [Node(a), Node(b)],
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, a, b, 1),
            Link(3, MeshNodeKey.Gateway, b, 99)
        ]);
        MeshGraphSnapshot after = Graph(8, [Node(a), Node(b)],
        [
            Link(1, MeshNodeKey.Gateway, a, 1),
            Link(2, a, b, 1),
            Link(3, MeshNodeKey.Gateway, b, 100)
        ]);
        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(before);

        MeshSpanningTreeRefresh refreshed =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, after);

        Assert.True(refreshed.HasGraphRevisionChanged);
        Assert.False(refreshed.HasBackboneChanged);
        Assert.Empty(refreshed.RouteTransitions);
        Assert.Equal(2d, refreshed.Current.TotalSimulatedEnergyCost);
    }

    [Fact]
    public void UnchangedGraphRevision_ReusesPriorImmutableProjection()
    {
        MeshNodeKey a = Sensor(1);
        MeshGraphSnapshot unchanged = Graph(55, [Node(a)],
            [Link(1, MeshNodeKey.Gateway, a, 2)]);
        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(unchanged);

        MeshSpanningTreeRefresh refreshed =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, unchanged);

        Assert.Same(original, refreshed.Current);
        Assert.False(refreshed.HasGraphRevisionChanged);
        Assert.Empty(refreshed.RouteTransitions);
    }

    [Fact]
    public void IslandLinkRemoval_ReportsComponentSplitEvenWithoutGatewayChanges()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot before = Graph(3, [Node(a), Node(b)],
            [Link(1, a, b, 1)]);
        MeshGraphSnapshot after = Graph(4, [Node(a), Node(b)], []);
        MeshGatewaySpanningTreeRouting original =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(before);

        MeshSpanningTreeRefresh refreshed =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(original, after);

        Assert.True(refreshed.HasDisconnectedComponentsChanged);
        Assert.False(refreshed.HasBackboneChanged);
        Assert.Empty(refreshed.RouteTransitions);
        Assert.Equal(2, refreshed.Current.DisconnectedComponents.Count);
    }

    [Fact]
    public void OutOfOrderSnapshotAndCancelledWork_FailBeforePublishingResult()
    {
        MeshNodeKey a = Sensor(1);
        MeshGatewaySpanningTreeRouting previous =
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRouting(
                Graph(9, [Node(a)], [Link(1, MeshNodeKey.Gateway, a, 2)]));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(
                previous, MeshGraphSnapshot.GatewayOnly(8)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            MeshSpanningTreeService.CalculateGatewaySpanningTreeRefresh(
                previous, MeshGraphSnapshot.GatewayOnly(10), cancellation.Token));
    }

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse($"00000000-0000-0000-0000-{ordinal:X12}"));

    private static MeshNode Node(
        MeshNodeKey sensor,
        SensorStatus status = SensorStatus.Online) => new(sensor, status);

    private static MeshLink Link(
        int ordinal, MeshNodeKey first, MeshNodeKey second, double cost) =>
        new(Guid.Parse($"20000000-0000-0000-0000-{ordinal:X12}"), first, second, cost);

    private static MeshGraphSnapshot Graph(
        long version,
        MeshNode[] sensors,
        MeshLink[] links) =>
        new(version, new[] { MeshNode.Gateway }.Concat(sensors), links);
}