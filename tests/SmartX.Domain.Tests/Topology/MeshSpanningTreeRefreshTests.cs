using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-06 Commit 3: immutable before/after contracts. These assertions avoid
/// guessing from graph-version increments whether a sensor's route changed.
/// </summary>
public sealed class MeshSpanningTreeRefreshTests
{
    [Fact]
    public void SameProjection_IsNoOpWithoutAnyInventedRouteTransition()
    {
        MeshGatewaySpanningTreeRouting previous = GatewayOnly(7);

        var result = new MeshSpanningTreeRefresh(previous, previous);

        Assert.Same(previous, result.Previous);
        Assert.Same(previous, result.Current);
        Assert.False(result.HasGraphRevisionChanged);
        Assert.False(result.HasBackboneChanged);
        Assert.False(result.HasDisconnectedComponentsChanged);
        Assert.Empty(result.RouteTransitions);
    }

    [Fact]
    public void SameSelectedLinkWithNewWeight_IsCostChangeNotReroute()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewaySpanningTreeRouting before = Connected(11, sensor, 2);
        MeshGatewaySpanningTreeRouting after = Connected(12, sensor, 5);

        var result = new MeshSpanningTreeRefresh(before, after);
        MeshTreeRouteTransition transition = Assert.Single(result.RouteTransitions);

        Assert.True(result.HasGraphRevisionChanged);
        Assert.True(result.HasBackboneChanged);
        Assert.Equal(sensor, transition.Sensor);
        Assert.True(transition.HasCostChange);
        Assert.False(transition.WasRerouted);
        Assert.False(transition.LostGatewayConnectivity);
        Assert.Empty(result.ReroutedSensors);
        Assert.Equal(2d, transition.PreviousRoute?.AccumulatedSimulatedEnergyCost);
        Assert.Equal(5d, transition.CurrentRoute?.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public void BridgeLossAndRecovery_DistinguishesDisconnectionFromNewRegistration()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewaySpanningTreeRouting connected = Connected(1, sensor, 1);
        MeshGatewaySpanningTreeRouting disconnected = Disconnected(2, sensor);
        MeshGatewaySpanningTreeRouting restored = Connected(3, sensor, 1);

        var loss = new MeshSpanningTreeRefresh(connected, disconnected);
        var recovery = new MeshSpanningTreeRefresh(disconnected, restored);

        Assert.Equal(new[] { sensor }, loss.LostGatewayConnectivitySensors);
        Assert.Empty(loss.RegainedGatewayConnectivitySensors);
        Assert.Equal(new[] { sensor }, recovery.RegainedGatewayConnectivitySensors);
        Assert.Empty(recovery.NewlyRegisteredSensors);
        Assert.Null(Assert.Single(loss.RouteTransitions).CurrentRoute?.HopCount);
        Assert.Equal(MeshRouteStatus.Disconnected,
            Assert.Single(loss.RouteTransitions).CurrentRoute?.Status);
    }

    [Fact]
    public void AddedThenRemovedSensor_IsNotConfusedWithSensorHealth()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewaySpanningTreeRouting original = GatewayOnly(0);
        MeshGatewaySpanningTreeRouting registered = Disconnected(1, sensor);
        MeshGatewaySpanningTreeRouting removed = GatewayOnly(2);

        var added = new MeshSpanningTreeRefresh(original, registered);
        var deleted = new MeshSpanningTreeRefresh(registered, removed);

        Assert.Equal(new[] { sensor }, added.NewlyRegisteredSensors);
        Assert.True(Assert.Single(added.RouteTransitions).WasRegistered);
        Assert.Equal(new[] { sensor }, deleted.RemovedSensors);
        Assert.True(Assert.Single(deleted.RouteTransitions).WasRemoved);
        Assert.Empty(added.RegainedGatewayConnectivitySensors);
        Assert.Empty(deleted.LostGatewayConnectivitySensors);
    }

    [Fact]
    public void DisconnectedIslandMerge_IsReportedEvenWhenTreeRoutesAreUnchanged()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGatewaySpanningTreeRouting before = new(
            new MinimumSpanningTreeResult(10, []),
            [a, b], [],
            [new MeshDisconnectedComponent([a]), new MeshDisconnectedComponent([b])]);
        MeshGatewaySpanningTreeRouting after = new(
            new MinimumSpanningTreeResult(11, []),
            [a, b], [],
            [new MeshDisconnectedComponent([a, b])]);

        var refresh = new MeshSpanningTreeRefresh(before, after);

        Assert.True(refresh.HasGraphRevisionChanged);
        Assert.False(refresh.HasBackboneChanged);
        Assert.True(refresh.HasDisconnectedComponentsChanged);
        Assert.Empty(refresh.RouteTransitions);
        Assert.Equal(2, refresh.Current.DisconnectedSensorCount);
    }

    [Fact]
    public void OfflineStatusClassificationChange_IsVisibleWithoutAnyTreeEdgeChange()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewaySpanningTreeRouting before = Disconnected(5, sensor);
        MeshGatewaySpanningTreeRouting after = new(
            new MinimumSpanningTreeResult(6, []),
            [sensor], [sensor], []);

        var refresh = new MeshSpanningTreeRefresh(before, after);
        MeshTreeRouteTransition transition = Assert.Single(refresh.RouteTransitions);

        Assert.False(refresh.HasBackboneChanged);
        Assert.Equal(MeshRouteStatus.Disconnected, transition.PreviousRoute?.Status);
        Assert.Equal(MeshRouteStatus.Unavailable, transition.CurrentRoute?.Status);
        Assert.False(transition.LostGatewayConnectivity);
        Assert.False(transition.RegainedGatewayConnectivity);
        Assert.Empty(refresh.ReroutedSensors);
    }

    [Fact]
    public void EqualVersionsWithConflictingConnectivity_AreRejected()
    {
        MeshNodeKey sensor = Sensor(1);
        MeshGatewaySpanningTreeRouting disconnected = Disconnected(8, sensor);
        MeshGatewaySpanningTreeRouting connected = Connected(8, sensor, 1);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshSpanningTreeRefresh(disconnected, connected));
    }

    [Fact]
    public void RouteTransitions_AreDefensivelyReadOnlyAndSortedBySensorKey()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGatewaySpanningTreeRouting before = GatewayOnly(5);
        MeshGatewaySpanningTreeRouting after = new(
            new MinimumSpanningTreeResult(6, []),
            [a, b], [],
            [new MeshDisconnectedComponent([a]), new MeshDisconnectedComponent([b])]);

        var result = new MeshSpanningTreeRefresh(before, after);

        Assert.Equal(new[] { a, b }, result.RouteTransitions.Select(t => t.Sensor));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshTreeRouteTransition>)result.RouteTransitions).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)result.NewlyRegisteredSensors).Clear());
    }

    [Fact]
    public void InvalidTransitionsAndRegressiveGraphVersions_AreRejected()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGatewaySpanningTreeRouting after = Connected(6, a, 1);
        MeshTreeRoute? route = null;
        Assert.True(after.TryGetTreeRouteToGateway(a, out route));
        Assert.NotNull(route);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRouteTransition(default, null, route));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRouteTransition(b, null, route));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRouteTransition(a, null, null));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRouteTransition(a, route, route));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshSpanningTreeRefresh(after, GatewayOnly(5)));
    }

    private static MeshGatewaySpanningTreeRouting GatewayOnly(long version) =>
        new(new MinimumSpanningTreeResult(version, []), [], [], []);

    private static MeshGatewaySpanningTreeRouting Connected(
        long version,
        MeshNodeKey sensor,
        double weight) =>
        new(new MinimumSpanningTreeResult(version,
                [new MeshTreeBranch(MeshNodeKey.Gateway, sensor,
                    new MeshLink(LinkId(1), MeshNodeKey.Gateway, sensor, weight))]),
            [sensor], [], []);

    private static MeshGatewaySpanningTreeRouting Disconnected(
        long version,
        MeshNodeKey sensor) =>
        new(new MinimumSpanningTreeResult(version, []),
            [sensor], [], [new MeshDisconnectedComponent([sensor])]);

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse($"00000000-0000-0000-0000-{ordinal:X12}"));

    private static Guid LinkId(int ordinal) =>
        Guid.Parse($"10000000-0000-0000-0000-{ordinal:X12}");
}