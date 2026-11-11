using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-06 Commit 4: numerical self-healing proofs over the real in-memory
/// sensor registry and synchronized adjacency-list store. Only explicit
/// configured links may appear in a recalculated MST. Each refresh reads
/// a new authoritative immutable graph revision and preserves the old one.
/// </summary>
public sealed class MeshPrimSelfHealingAdversarialTests
{
    [Fact]
    public async Task ThreeRelayFailures_UseEachRealBackupThenReportDisconnection()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile preferred = await OnlineAsync(fixture, 301);
        SensorProfile backup = await OnlineAsync(fixture, 302);
        SensorProfile lastResort = await OnlineAsync(fixture, 303);
        SensorProfile leafProfile = await OnlineAsync(fixture, 304);
        MeshNodeKey a = MeshTopologyScenario.Key(preferred);
        MeshNodeKey b = MeshTopologyScenario.Key(backup);
        MeshNodeKey c = MeshTopologyScenario.Key(lastResort);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafProfile);

        await AddAsync(fixture, 401, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 402, a, leaf, 1);
        await AddAsync(fixture, 403, MeshNodeKey.Gateway, b, 2);
        await AddAsync(fixture, 404, b, leaf, 2);
        await AddAsync(fixture, 405, MeshNodeKey.Gateway, c, 3);
        await AddAsync(fixture, 406, c, leaf, 4);

        MeshGatewaySpanningTreeRouting original =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        Assert.Equal(7d, original.TotalSimulatedEnergyCost);
        AssertRoute(original, leaf, [leaf, a, MeshNodeKey.Gateway], 2d);
        Assert.Equal(4, original.ReachableSensorCount);
        Assert.Equal(4, original.Tree.SelectedEdgeCount);
        Assert.Equal(6, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        // First failure: cheaper GW-A-A-leaf path disappears, so the
        // explicitly configured B alternative is selected by Prim.
        await fixture.SetStatusAsync(preferred, SensorStatus.Offline, 2);
        MeshSpanningTreeRefresh firstFailure =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(original);
        Assert.True(firstFailure.HasBackboneChanged);
        Assert.Equal(7d, firstFailure.Current.TotalSimulatedEnergyCost);
        AssertRoute(firstFailure.Current, leaf,
            [leaf, b, MeshNodeKey.Gateway], 4d);
        Assert.Contains(leaf, firstFailure.ReroutedSensors);
        Assert.Contains(a, firstFailure.LostGatewayConnectivitySensors);

        // Second failure: B is also unusable, but the real C backup exists.
        await fixture.SetStatusAsync(backup, SensorStatus.Fault, 2);
        MeshSpanningTreeRefresh secondFailure =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(firstFailure.Current);
        Assert.Equal(7d, secondFailure.Current.TotalSimulatedEnergyCost);
        AssertRoute(secondFailure.Current, leaf,
            [leaf, c, MeshNodeKey.Gateway], 7d);
        Assert.Contains(leaf, secondFailure.ReroutedSensors);
        Assert.Contains(b, secondFailure.LostGatewayConnectivitySensors);

        // Third failure: no healthy relay remains. No invented fallback
        // route is permitted and the selected tree is gateway-only.
        await fixture.SetStatusAsync(lastResort, SensorStatus.Maintenance, 2);
        MeshSpanningTreeRefresh allFailed =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(secondFailure.Current);
        Assert.Equal(0d, allFailed.Current.TotalSimulatedEnergyCost);
        Assert.Empty(allFailed.Current.Tree.SelectedLinks);
        Assert.True(allFailed.Current.TryGetTreeRouteToGateway(
            leaf, out MeshTreeRoute? disconnected));
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);
        Assert.Empty(disconnected.PathToGateway);
        Assert.Null(disconnected.AccumulatedSimulatedEnergyCost);
        Assert.Contains(leaf, allFailed.LostGatewayConnectivitySensors);
        Assert.Equal(6, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        // Recover only B: the two explicitly configured B links reconnect
        // B and the leaf. Historical projections stay unchanged.
        await fixture.SetStatusAsync(backup, SensorStatus.Online, 3);
        MeshSpanningTreeRefresh recovery =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(allFailed.Current);
        Assert.Equal(4d, recovery.Current.TotalSimulatedEnergyCost);
        AssertRoute(recovery.Current, leaf,
            [leaf, b, MeshNodeKey.Gateway], 4d);
        Assert.Contains(leaf, recovery.RegainedGatewayConnectivitySensors);
        Assert.Contains(b, recovery.RegainedGatewayConnectivitySensors);
        Assert.Equal(7d, original.TotalSimulatedEnergyCost);
        AssertRoute(original, leaf, [leaf, a, MeshNodeKey.Gateway], 2d);
        Assert.Equal(6, (await fixture.Store.GetSnapshotAsync()).Links.Count);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public async Task NonredundantRelayFailure_NeverClaimsSelfHealing(
        SensorStatus failedStatus)
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile bridgeProfile = await OnlineAsync(fixture, 311);
        SensorProfile leafProfile = await OnlineAsync(fixture, 312);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeProfile);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafProfile);
        await AddAsync(fixture, 410, MeshNodeKey.Gateway, bridge, 2);
        await AddAsync(fixture, 411, bridge, leaf, 3);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        Assert.Equal(5d, before.TotalSimulatedEnergyCost);
        AssertRoute(before, leaf,
            [leaf, bridge, MeshNodeKey.Gateway], 5d);

        await fixture.SetStatusAsync(bridgeProfile, failedStatus, 2);
        MeshSpanningTreeRefresh failed =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.Equal(0d, failed.Current.TotalSimulatedEnergyCost);
        Assert.Empty(failed.Current.Tree.SelectedLinks);
        Assert.True(failed.Current.TryGetTreeRouteToGateway(
            bridge, out MeshTreeRoute? unavailable));
        Assert.True(failed.Current.TryGetTreeRouteToGateway(
            leaf, out MeshTreeRoute? disconnected));
        Assert.NotNull(unavailable);
        Assert.NotNull(disconnected);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnected.Status);
        Assert.Null(disconnected.AccumulatedSimulatedEnergyCost);
        Assert.Empty(disconnected.PathToGateway);
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        await fixture.SetStatusAsync(bridgeProfile, SensorStatus.Online, 3);
        MeshSpanningTreeRefresh recovered =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(failed.Current);
        Assert.Equal(5d, recovered.Current.TotalSimulatedEnergyCost);
        AssertRoute(recovered.Current, leaf,
            [leaf, bridge, MeshNodeKey.Gateway], 5d);
        Assert.Contains(leaf, recovered.RegainedGatewayConnectivitySensors);
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync()).Links.Count);
    }

    [Fact]
    public async Task RemovingIslandBridge_SplitsComponent_AndReaddingItRestoresConnectivity()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile first = await OnlineAsync(fixture, 321);
        SensorProfile second = await OnlineAsync(fixture, 322);
        SensorProfile third = await OnlineAsync(fixture, 323);
        SensorProfile fourth = await OnlineAsync(fixture, 324);
        MeshNodeKey a = MeshTopologyScenario.Key(first);
        MeshNodeKey b = MeshTopologyScenario.Key(second);
        MeshNodeKey c = MeshTopologyScenario.Key(third);
        MeshNodeKey d = MeshTopologyScenario.Key(fourth);

        await AddAsync(fixture, 420, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 421, a, b, 1);
        await AddAsync(fixture, 422, b, c, 3);
        await AddAsync(fixture, 423, c, d, 1);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        Assert.Equal(6d, before.TotalSimulatedEnergyCost);
        AssertRoute(before, d,
            [d, c, b, a, MeshNodeKey.Gateway], 6d);

        Assert.True(await fixture.Store.RemoveLinkAsync(
            MeshTopologyScenario.LinkId(422)));
        MeshSpanningTreeRefresh split =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.Equal(2d, split.Current.TotalSimulatedEnergyCost);
        Assert.Equal(2, split.Current.DisconnectedSensorCount);
        MeshDisconnectedComponent component =
            Assert.Single(split.Current.DisconnectedComponents);
        Assert.Equal(new[] { c, d }.OrderBy(key => key), component.Sensors);
        Assert.Equal(new[] { c, d }.OrderBy(key => key),
            split.LostGatewayConnectivitySensors);
        Assert.Equal(3, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        // Reintroducing an explicit bridge is a real operator graph mutation,
        // not an automatic link invented by Prim. A higher weight increases
        // the selected backbone cost without affecting the reachability rule.
        await AddAsync(fixture, 424, b, c, 5);
        MeshSpanningTreeRefresh restored =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(split.Current);
        Assert.Equal(8d, restored.Current.TotalSimulatedEnergyCost);
        Assert.Empty(restored.Current.DisconnectedComponents);
        Assert.Equal(new[] { c, d }.OrderBy(key => key),
            restored.RegainedGatewayConnectivitySensors);
        AssertRoute(restored.Current, d,
            [d, c, b, a, MeshNodeKey.Gateway], 8d);
        Assert.Equal(4, (await fixture.Store.GetSnapshotAsync()).Links.Count);
        AssertRoute(before, d,
            [d, c, b, a, MeshNodeKey.Gateway], 6d);
    }

    [Fact]
    public async Task CostChangeOnUnusedEdge_IsNotMistakenForAChangedRoute()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile first = await OnlineAsync(fixture, 331);
        SensorProfile second = await OnlineAsync(fixture, 332);
        MeshNodeKey a = MeshTopologyScenario.Key(first);
        MeshNodeKey b = MeshTopologyScenario.Key(second);
        await AddAsync(fixture, 430, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 431, a, b, 1);
        await AddAsync(fixture, 432, MeshNodeKey.Gateway, b, 9);
        MeshGatewaySpanningTreeRouting initial =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        AssertRoute(initial, b, [b, a, MeshNodeKey.Gateway], 2d);

        await fixture.Store.UpdateLinkCostAsync(
            MeshTopologyScenario.LinkId(432), 8);
        MeshSpanningTreeRefresh unchanged =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(initial);
        Assert.True(unchanged.HasGraphRevisionChanged);
        Assert.False(unchanged.HasBackboneChanged);
        Assert.Empty(unchanged.RouteTransitions);
        Assert.Equal(2d, unchanged.Current.TotalSimulatedEnergyCost);
        AssertRoute(unchanged.Current, b,
            [b, a, MeshNodeKey.Gateway], 2d);

        await fixture.Store.UpdateLinkCostAsync(
            MeshTopologyScenario.LinkId(432), 0.5);
        MeshSpanningTreeRefresh switched =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(unchanged.Current);
        Assert.True(switched.HasBackboneChanged);
        Assert.Equal(1.5d, switched.Current.TotalSimulatedEnergyCost);
        Assert.Contains(b, switched.ReroutedSensors);
        AssertRoute(switched.Current, b,
            [b, MeshNodeKey.Gateway], 0.5d);
        Assert.Equal(3, (await fixture.Store.GetSnapshotAsync()).Links.Count);
    }

    private static async Task<SensorProfile> OnlineAsync(
        MeshTopologyScenario fixture,
        int ordinal)
    {
        SensorProfile profile = await fixture.RegisterAsync(ordinal);
        await fixture.SetStatusAsync(profile, SensorStatus.Online, 1);
        return profile;
    }

    private static async Task AddAsync(
        MeshTopologyScenario fixture,
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double cost)
    {
        _ = await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(ordinal, first, second, cost));
    }

    private static void AssertRoute(
        MeshGatewaySpanningTreeRouting routing,
        MeshNodeKey sensor,
        MeshNodeKey[] expectedPath,
        double expectedCost)
    {
        Assert.True(routing.TryGetTreeRouteToGateway(
            sensor, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(expectedPath, route.PathToGateway);
        Assert.Equal(expectedPath.Length - 1, route.HopCount);
        Assert.Equal(expectedCost, route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(routing.GraphVersion, route.GraphVersion);
        Assert.Equal(expectedCost,
            route.SelectedLinksToGateway.Sum(link => link.SimulatedEnergyCost));
    }
}