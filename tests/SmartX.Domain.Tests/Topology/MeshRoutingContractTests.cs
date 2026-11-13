using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-05 Commit 2: immutable route and disconnected-component contracts.
/// Rejected states cannot be accidentally exposed to the topology API later.
/// </summary>
public sealed class MeshRoutingContractTests
{
    private static readonly MeshNodeKey First = Sensor(1);
    private static readonly MeshNodeKey Second = Sensor(2);
    private static readonly MeshNodeKey Third = Sensor(3);

    [Fact]
    public void ReachableRoute_IsSensorToGatewayAndUsesLinkCount()
    {
        var route = new MeshRoute(12, Second, MeshRouteStatus.Reachable,
            [Second, First, MeshNodeKey.Gateway]);

        Assert.Equal(12, route.GraphVersion);
        Assert.Equal(Second, route.Sensor);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(2, route.HopCount);
        Assert.Equal(new[] { Second, First, MeshNodeKey.Gateway },
            route.PathToGateway);
    }

    [Theory]
    [InlineData(MeshRouteStatus.Disconnected)]
    [InlineData(MeshRouteStatus.Unavailable)]
    public void NonReachableRoute_HasNoFabricatedPathOrHopCount(MeshRouteStatus status)
    {
        var route = new MeshRoute(2, First, status, []);

        Assert.Equal(status, route.Status);
        Assert.Null(route.HopCount);
        Assert.Empty(route.PathToGateway);
    }

    [Fact]
    public void Route_RejectsInvalidVersionSensorAndStatus()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(-1, First, MeshRouteStatus.Disconnected, []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, default, MeshRouteStatus.Disconnected, []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, MeshNodeKey.Gateway, MeshRouteStatus.Disconnected, []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, (MeshRouteStatus)255, []));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable, null!));
    }

    [Fact]
    public void ReachableRoute_RejectsReversePathsCyclesAndInvalidKeys()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable, []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable,
                [MeshNodeKey.Gateway, First]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable,
                [First, Second]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable,
                [First, Second, First, MeshNodeKey.Gateway]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Reachable,
                [First, default, MeshNodeKey.Gateway]));
    }

    [Fact]
    public void NonReachableRoute_RejectsAnySuppliedPartialPath()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Disconnected, [First]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshRoute(0, First, MeshRouteStatus.Unavailable,
                [First, MeshNodeKey.Gateway]));
    }

    [Fact]
    public void Route_DefensivelyCopiesThePathAndBlocksMutation()
    {
        var input = new List<MeshNodeKey> { First, MeshNodeKey.Gateway };
        var route = new MeshRoute(5, First, MeshRouteStatus.Reachable, input);
        input.Clear();

        Assert.Equal(new[] { First, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)route.PathToGateway)[0] = Third);
    }

    [Fact]
    public void DisconnectedComponent_RequiresUniqueOrderedSensorKeys()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MeshDisconnectedComponent(null!));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshDisconnectedComponent([]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshDisconnectedComponent([MeshNodeKey.Gateway]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshDisconnectedComponent([First, First]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshDisconnectedComponent([Second, First]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshDisconnectedComponent([default]));
    }

    [Fact]
    public void DisconnectedComponent_CopiesAndProtectsItsSortedMembership()
    {
        var input = new List<MeshNodeKey> { First, Second };
        var component = new MeshDisconnectedComponent(input);
        input.RemoveAt(0);

        Assert.Equal(new[] { First, Second }, component.Sensors);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)component.Sensors)[0] = Third);
    }

    [Fact]
    public void GatewayRouting_ProducesTypedRoutesAndDistinguishesMissingSensors()
    {
        var routing = SampleRouting();

        Assert.Equal(9, routing.GraphVersion);
        Assert.Equal(3, routing.RegisteredSensorCount);
        Assert.Equal(1, routing.ReachableSensorCount);
        Assert.Equal(1, routing.UnavailableSensorCount);
        Assert.Equal(1, routing.DisconnectedSensorCount);
        Assert.Equal(new[] { First, Second, Third }, routing.RegisteredSensors);
        Assert.Equal(new[] { Second }, routing.UnavailableSensors);
        Assert.Equal(new[] { Third }, Assert.Single(routing.DisconnectedComponents).Sensors);

        Assert.True(routing.TryGetRouteToGateway(First, out MeshRoute? reachable));
        Assert.NotNull(reachable);
        Assert.Equal(MeshRouteStatus.Reachable, reachable.Status);
        Assert.Equal(new[] { First, MeshNodeKey.Gateway }, reachable.PathToGateway);

        Assert.True(routing.TryGetRouteToGateway(Second, out MeshRoute? unavailable));
        Assert.NotNull(unavailable);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);

        Assert.True(routing.TryGetRouteToGateway(Third, out MeshRoute? disconnected));
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);

        Assert.False(routing.TryGetRouteToGateway(Sensor(99), out MeshRoute? missing));
        Assert.Null(missing);
        Assert.Throws<InvalidMeshTopologyException>(() =>
            routing.TryGetRouteToGateway(default, out _));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            routing.TryGetRouteToGateway(MeshNodeKey.Gateway, out _));
    }

    [Fact]
    public void GatewayRouting_RejectsOverlappingOrIncompleteClassifications()
    {
        var reachability = Reachability();
        var parents = Parents();

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [First], [new MeshDisconnectedComponent([Third])], parents));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [Second], [], parents));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [Second], [new MeshDisconnectedComponent([Second])], parents));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [Third, Second, First],
                [Second], [new MeshDisconnectedComponent([Third])], parents));
    }

    [Fact]
    public void GatewayRouting_RejectsMissingExtraOrWrongLevelPredecessors()
    {
        var reachability = Reachability();
        var components = new[] { new MeshDisconnectedComponent([Third]) };

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [Second], components, new Dictionary<MeshNodeKey, MeshNodeKey>()));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [Second], components, new Dictionary<MeshNodeKey, MeshNodeKey>
                {
                    [First] = Second
                }));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayRouting(reachability, [First, Second, Third],
                [Second], components, new Dictionary<MeshNodeKey, MeshNodeKey>
                {
                    [First] = MeshNodeKey.Gateway,
                    [Third] = First
                }));
    }

    [Fact]
    public void GatewayRouting_CopiesParentMapAndSensorCollections()
    {
        var suppliedParents = Parents();
        var suppliedSensors = new List<MeshNodeKey> { First, Second, Third };
        var routing = new MeshGatewayRouting(Reachability(), suppliedSensors,
            [Second], [new MeshDisconnectedComponent([Third])], suppliedParents);

        suppliedParents[First] = Second;
        suppliedSensors.Clear();

        Assert.Equal(3, routing.RegisteredSensorCount);
        Assert.True(routing.TryGetRouteToGateway(First, out MeshRoute? route));
        Assert.NotNull(route);
        Assert.Equal(new[] { First, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)routing.RegisteredSensors)[0] = Third);
    }

    private static MeshGatewayRouting SampleRouting() => new(
        Reachability(),
        [First, Second, Third],
        [Second],
        [new MeshDisconnectedComponent([Third])],
        Parents());

    private static MeshGatewayReachability Reachability() => new(9,
    [
        new MeshNodeHopCount(MeshNodeKey.Gateway, 0),
        new MeshNodeHopCount(First, 1)
    ]);

    private static Dictionary<MeshNodeKey, MeshNodeKey> Parents() => new()
    {
        [First] = MeshNodeKey.Gateway
    };

    private static MeshNodeKey Sensor(int ordinal) => MeshNodeKey.ForSensor(
        Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}"));
}