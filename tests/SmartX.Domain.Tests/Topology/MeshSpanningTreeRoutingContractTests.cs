using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-06 Commit 2: protect weighted tree paths and their immutable,
/// exhaustive sensor connectivity summary. These tests use no service mocks;
/// the domain contract itself must reject misleading path/cost projections.
/// </summary>
public sealed class MeshSpanningTreeRoutingContractTests
{
    private static readonly MeshNodeKey A = Sensor(1);
    private static readonly MeshNodeKey B = Sensor(2);
    private static readonly MeshNodeKey C = Sensor(3);
    private static readonly MeshNodeKey D = Sensor(4);

    [Fact]
    public void ValidSummary_SeparatesBackboneCostFromOneSensorRouteCost()
    {
        MeshGatewaySpanningTreeRouting routing = SampleSummary();

        Assert.Equal(30, routing.GraphVersion);
        Assert.Equal(4, routing.RegisteredSensorCount);
        Assert.Equal(2, routing.ReachableSensorCount);
        Assert.Equal(1, routing.UnavailableSensorCount);
        Assert.Equal(1, routing.DisconnectedSensorCount);
        Assert.Equal(2, routing.SelectedEdgeCount);
        Assert.Equal(3d, routing.TotalSimulatedEnergyCost);
        Assert.Equal(new[] { A, B, C, D }, routing.RegisteredSensors);
        Assert.Equal(new[] { C }, routing.UnavailableSensors);
        Assert.Equal(new[] { D }, Assert.Single(routing.DisconnectedComponents).Sensors);

        Assert.True(routing.TryGetTreeRouteToGateway(B, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(30, route.GraphVersion);
        Assert.Equal(new[] { B, A, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Equal(new[] { LinkId(2), LinkId(1) },
            route.SelectedLinksToGateway.Select(link => link.Id));
        Assert.Equal(2, route.HopCount);
        Assert.Equal(3d, route.AccumulatedSimulatedEnergyCost);

        Assert.True(routing.TryGetTreeRouteToGateway(A, out MeshTreeRoute? direct));
        Assert.NotNull(direct);
        Assert.Equal(1, direct.HopCount);
        Assert.Equal(1d, direct.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public void UnavailableAndDisconnectedRoutes_HaveNullCostAndNoPartialPath()
    {
        MeshGatewaySpanningTreeRouting routing = SampleSummary();

        Assert.True(routing.TryGetTreeRouteToGateway(C, out MeshTreeRoute? unavailable));
        Assert.NotNull(unavailable);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.HopCount);
        Assert.Null(unavailable.AccumulatedSimulatedEnergyCost);
        Assert.Empty(unavailable.PathToGateway);
        Assert.Empty(unavailable.SelectedLinksToGateway);

        Assert.True(routing.TryGetTreeRouteToGateway(D, out MeshTreeRoute? disconnected));
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);
        Assert.Null(disconnected.HopCount);
        Assert.Null(disconnected.AccumulatedSimulatedEnergyCost);
        Assert.Empty(disconnected.PathToGateway);
        Assert.Empty(disconnected.SelectedLinksToGateway);
    }

    [Fact]
    public void UnknownSensor_ReturnsFalseWithoutFabricatingAStatus()
    {
        MeshGatewaySpanningTreeRouting routing = SampleSummary();

        Assert.False(routing.TryGetTreeRouteToGateway(Sensor(99), out MeshTreeRoute? route));
        Assert.Null(route);
        Assert.Throws<InvalidMeshTopologyException>(() =>
            routing.TryGetTreeRouteToGateway(default, out _));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            routing.TryGetTreeRouteToGateway(MeshNodeKey.Gateway, out _));
    }

    [Fact]
    public void GatewayOnlySummary_DoesNotInventRegisteredDevices()
    {
        var routing = new MeshGatewaySpanningTreeRouting(
            new MinimumSpanningTreeResult(7, []), [], [], []);

        Assert.Equal(7, routing.GraphVersion);
        Assert.Equal(0d, routing.TotalSimulatedEnergyCost);
        Assert.Equal(0, routing.RegisteredSensorCount);
        Assert.Equal(0, routing.SelectedEdgeCount);
        Assert.Empty(routing.DisconnectedComponents);
        Assert.False(routing.TryGetTreeRouteToGateway(A, out _));
    }

    [Fact]
    public void TreeRoute_CopiesCollectionsAndDoesNotExposeWritableViews()
    {
        MeshLink first = Link(1, MeshNodeKey.Gateway, A, 1);
        var path = new List<MeshNodeKey> { A, MeshNodeKey.Gateway };
        var links = new List<MeshLink> { first };
        var route = new MeshTreeRoute(5, A, MeshRouteStatus.Reachable, path, links);

        path.Clear();
        links.Clear();

        Assert.Equal(new[] { A, MeshNodeKey.Gateway }, route.PathToGateway);
        Assert.Same(first, Assert.Single(route.SelectedLinksToGateway));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)route.PathToGateway)[0] = B);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshLink>)route.SelectedLinksToGateway)[0] =
                Link(8, A, B, 1));
    }

    [Fact]
    public void Summary_CopiesRegisteredAndUnavailableInputs()
    {
        var registered = new List<MeshNodeKey> { A, B, C, D };
        var unavailable = new List<MeshNodeKey> { C };
        var components = new List<MeshDisconnectedComponent>
        {
            new MeshDisconnectedComponent([D])
        };
        var result = new MeshGatewaySpanningTreeRouting(
            TwoSensorTree(), registered, unavailable, components);

        registered.Clear();
        unavailable.Clear();
        components.Clear();

        Assert.Equal(4, result.RegisteredSensorCount);
        Assert.Equal(new[] { C }, result.UnavailableSensors);
        Assert.Equal(new[] { D }, Assert.Single(result.DisconnectedComponents).Sensors);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)result.RegisteredSensors)[0] = D);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)result.UnavailableSensors)[0] = A);
    }

    [Fact]
    public void RouteRejectsNegativeVersionInvalidSensorOrUnknownStatus()
    {
        MeshLink link = Link(1, MeshNodeKey.Gateway, A, 1);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(-1, A, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], [link]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, default, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], [link]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, MeshNodeKey.Gateway, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], [link]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, (MeshRouteStatus)255,
                [A, MeshNodeKey.Gateway], [link]));
    }

    [Fact]
    public void RouteRejectsNullInputAndMalformedPaths()
    {
        MeshLink link = Link(1, MeshNodeKey.Gateway, A, 1);

        Assert.Throws<ArgumentNullException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable, null!, [link]));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], null!));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable, [], []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [MeshNodeKey.Gateway, A], [link]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, B, A, MeshNodeKey.Gateway], [link, link, link]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, default, MeshNodeKey.Gateway], [link, link]));
    }

    [Fact]
    public void RouteRejectsMissingMismatchedOrNullLinks()
    {
        MeshLink wrong = Link(1, MeshNodeKey.Gateway, B, 1);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], [wrong]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, MeshNodeKey.Gateway], new MeshLink[] { null! }));
    }

    [Theory]
    [InlineData(MeshRouteStatus.Disconnected)]
    [InlineData(MeshRouteStatus.Unavailable)]
    public void FailedRouteRejectsAnInventedPathOrSelectedLink(MeshRouteStatus status)
    {
        MeshLink link = Link(1, MeshNodeKey.Gateway, A, 1);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, status,
                [A, MeshNodeKey.Gateway], []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, status, [], [link]));
    }

    [Fact]
    public void RouteRejectsAccumulatedCostOverflow()
    {
        MeshLink first = Link(1, A, B, double.MaxValue);
        MeshLink second = Link(2, B, MeshNodeKey.Gateway, double.MaxValue);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeRoute(1, A, MeshRouteStatus.Reachable,
                [A, B, MeshNodeKey.Gateway], [first, second]));
    }

    [Fact]
    public void SummaryRejectsNullTreeOrInputCollections()
    {
        MinimumSpanningTreeResult tree = TwoSensorTree();

        Assert.Throws<ArgumentNullException>(() =>
            new MeshGatewaySpanningTreeRouting(null!, [A, B], [], []));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, null!, [], []));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B], null!, []));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B], [], null!));
    }

    [Fact]
    public void SummaryRejectsUnregisteredTreeMembersAndIncompletePartition()
    {
        MinimumSpanningTreeResult tree = TwoSensorTree();

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A], [], []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B, C], [], []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [B, A, C], [C], []));
    }

    [Fact]
    public void SummaryRejectsOverlapAndMalformedDisconnectedComponents()
    {
        MinimumSpanningTreeResult tree = TwoSensorTree();
        var one = new MeshDisconnectedComponent([C]);
        var two = new MeshDisconnectedComponent([D]);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B, C, D], [B], [one, two]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B, C, D], [C], [one]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B, C, D], [], [two, one]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewaySpanningTreeRouting(tree, [A, B, C, D], [],
                new MeshDisconnectedComponent[] { one, null! }));
    }

    private static MeshGatewaySpanningTreeRouting SampleSummary() => new(
        TwoSensorTree(),
        [A, B, C, D],
        [C],
        [new MeshDisconnectedComponent([D])]);

    private static MinimumSpanningTreeResult TwoSensorTree() => new(30,
    [
        new MeshTreeBranch(MeshNodeKey.Gateway, A,
            Link(1, MeshNodeKey.Gateway, A, 1)),
        new MeshTreeBranch(A, B, Link(2, A, B, 2))
    ]);

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:000000000000}"));

    private static Guid LinkId(int ordinal) =>
        Guid.Parse($"10000000-0000-0000-0000-{ordinal:000000000000}");

    private static MeshLink Link(
        int ordinal, MeshNodeKey first, MeshNodeKey second, double cost) =>
        new(LinkId(ordinal), first, second, cost);
}