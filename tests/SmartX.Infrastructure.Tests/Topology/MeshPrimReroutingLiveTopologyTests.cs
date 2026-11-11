using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-06 Commit 3: real sensor-registry and synchronized adjacency-store
/// integration. Mutations are performed ONLY through existing public
/// sensor/link operations. Prim never creates a hidden fallback radio edge.
/// </summary>
public sealed class MeshPrimReroutingLiveTopologyTests
{
    [Fact]
    public async Task OfflinePreferredRelay_ReroutesOverExistingBackupAndRestoresOnRecovery()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile preferred = await fixture.RegisterAsync(1);
        SensorProfile backup = await fixture.RegisterAsync(2);
        SensorProfile leafSensor = await fixture.RegisterAsync(3);
        MeshNodeKey a = MeshTopologyScenario.Key(preferred);
        MeshNodeKey b = MeshTopologyScenario.Key(backup);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await fixture.SetStatusAsync(preferred, SensorStatus.Online, 1);
        await fixture.SetStatusAsync(backup, SensorStatus.Online, 1);
        await fixture.SetStatusAsync(leafSensor, SensorStatus.Online, 1);
        await AddAsync(fixture, 201, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 202, a, leaf, 1);
        await AddAsync(fixture, 203, MeshNodeKey.Gateway, b, 2);
        await AddAsync(fixture, 204, b, leaf, 3);

        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        Assert.Equal(4d, before.TotalSimulatedEnergyCost);
        AssertPath(before, leaf, [leaf, a, MeshNodeKey.Gateway], 2);

        await fixture.SetStatusAsync(preferred, SensorStatus.Offline, 2);
        MeshSpanningTreeRefresh failure =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.True(failure.HasGraphRevisionChanged);
        Assert.True(failure.HasBackboneChanged);
        Assert.Equal(5d, failure.Current.TotalSimulatedEnergyCost);
        AssertPath(failure.Current, leaf, [leaf, b, MeshNodeKey.Gateway], 5);
        Assert.Equal(new[] { leaf }, failure.ReroutedSensors);
        Assert.Equal(new[] { a }, failure.LostGatewayConnectivitySensors);
        Assert.Equal(4, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        // Restoring a sensor's existing health never invents an extra edge;
        // Prim returns to the cheaper already-configured backbone.
        await fixture.SetStatusAsync(preferred, SensorStatus.Online, 3);
        MeshSpanningTreeRefresh recovery =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(failure.Current);
        Assert.True(recovery.CurrentGraphVersion > failure.CurrentGraphVersion);
        Assert.Equal(new[] { a }, recovery.RegainedGatewayConnectivitySensors);
        Assert.Equal(new[] { leaf }, recovery.ReroutedSensors);
        Assert.Equal(4d, recovery.Current.TotalSimulatedEnergyCost);
        AssertPath(recovery.Current, leaf, [leaf, a, MeshNodeKey.Gateway], 2);
        AssertPath(before, leaf, [leaf, a, MeshNodeKey.Gateway], 2);
        Assert.Equal(4, (await fixture.Store.GetSnapshotAsync()).Links.Count);
    }

    [Fact]
    public async Task RemovingPreferredLink_UsesOnlyRemainingExplicitBackup()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile first = await OnlineAsync(fixture, 4);
        SensorProfile second = await OnlineAsync(fixture, 5);
        SensorProfile leafProfile = await OnlineAsync(fixture, 6);
        MeshNodeKey a = MeshTopologyScenario.Key(first);
        MeshNodeKey b = MeshTopologyScenario.Key(second);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafProfile);
        await AddAsync(fixture, 205, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 206, a, leaf, 1);
        await AddAsync(fixture, 207, MeshNodeKey.Gateway, b, 2);
        await AddAsync(fixture, 208, b, leaf, 3);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        AssertPath(before, leaf, [leaf, a, MeshNodeKey.Gateway], 2);

        Assert.True(await fixture.Store.RemoveLinkAsync(MeshTopologyScenario.LinkId(206)));
        MeshSpanningTreeRefresh after =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);

        AssertPath(after.Current, leaf, [leaf, b, MeshNodeKey.Gateway], 5);
        Assert.Equal(6d, after.Current.TotalSimulatedEnergyCost);
        Assert.Contains(leaf, after.ReroutedSensors);
        MeshGraphSnapshot current = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(3, current.Links.Count);
        Assert.DoesNotContain(current.Links,
            link => link.Id == MeshTopologyScenario.LinkId(206));
        Assert.All(after.Current.Tree.SelectedLinks,
            link => Assert.Contains(current.Links, configured => configured.Id == link.Id));
    }

    [Fact]
    public async Task OnlyBridgeOffline_DoesNotPretendToSelfHeal()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile bridgeSensor = await OnlineAsync(fixture, 7);
        SensorProfile leafSensor = await OnlineAsync(fixture, 8);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await AddAsync(fixture, 209, MeshNodeKey.Gateway, bridge, 2);
        await AddAsync(fixture, 210, bridge, leaf, 2);
        MeshGatewaySpanningTreeRouting original =
            await prim.GetGatewaySpanningTreeRoutingAsync();

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Fault, 2);
        MeshSpanningTreeRefresh failed =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(original);
        Assert.Equal(0d, failed.Current.TotalSimulatedEnergyCost);
        Assert.Empty(failed.Current.Tree.SelectedLinks);
        Assert.Equal(new[] { bridge, leaf }.OrderBy(key => key).ToArray(),
            failed.LostGatewayConnectivitySensors);
        Assert.True(failed.Current.TryGetTreeRouteToGateway(leaf, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Disconnected, route.Status);
        Assert.Empty(route.PathToGateway);
        Assert.Null(route.AccumulatedSimulatedEnergyCost);
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online, 3);
        MeshSpanningTreeRefresh restored =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(failed.Current);
        Assert.Equal(new[] { bridge, leaf }.OrderBy(key => key).ToArray(),
            restored.RegainedGatewayConnectivitySensors);
        AssertPath(restored.Current, leaf, [leaf, bridge, MeshNodeKey.Gateway], 4);
    }

    [Fact]
    public async Task RealCostUpdate_ChangesSelectedTreeButDoesNotChangeLinkIdentity()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile first = await OnlineAsync(fixture, 9);
        SensorProfile second = await OnlineAsync(fixture, 10);
        MeshNodeKey a = MeshTopologyScenario.Key(first);
        MeshNodeKey b = MeshTopologyScenario.Key(second);
        await AddAsync(fixture, 211, MeshNodeKey.Gateway, a, 1);
        await AddAsync(fixture, 212, a, b, 1);
        await AddAsync(fixture, 213, MeshNodeKey.Gateway, b, 5);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        AssertPath(before, b, [b, a, MeshNodeKey.Gateway], 2);

        await fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(213), 0.5);
        MeshSpanningTreeRefresh updated =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.Equal(1.5d, updated.Current.TotalSimulatedEnergyCost);
        Assert.Equal(new[] { b }, updated.ReroutedSensors);
        AssertPath(updated.Current, b, [b, MeshNodeKey.Gateway], 0.5);
        AssertPath(before, b, [b, a, MeshNodeKey.Gateway], 2);
        Assert.Equal(3, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        // A no-op cost update retains the graph version and previous tree.
        long version = updated.CurrentGraphVersion;
        await fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(213), 0.5);
        MeshSpanningTreeRefresh unchanged =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(updated.Current);
        Assert.Equal(version, unchanged.CurrentGraphVersion);
        Assert.Same(updated.Current, unchanged.Current);
        Assert.Empty(unchanged.RouteTransitions);
    }

    [Fact]
    public async Task NewSensorDoesNotGainGatewayRouteUntilExplicitLinkAdded()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        MeshGatewaySpanningTreeRouting gatewayOnly =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        SensorProfile profile = await OnlineAsync(fixture, 11);
        MeshNodeKey sensor = MeshTopologyScenario.Key(profile);

        MeshSpanningTreeRefresh registered =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(gatewayOnly);
        Assert.Equal(new[] { sensor }, registered.NewlyRegisteredSensors);
        Assert.Equal(1, registered.Current.DisconnectedSensorCount);
        Assert.True(registered.Current.TryGetTreeRouteToGateway(sensor, out MeshTreeRoute? first));
        Assert.NotNull(first);
        Assert.Equal(MeshRouteStatus.Disconnected, first.Status);
        Assert.Empty((await fixture.Store.GetSnapshotAsync()).Links);

        await AddAsync(fixture, 214, MeshNodeKey.Gateway, sensor, 4);
        MeshSpanningTreeRefresh linked =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(registered.Current);
        Assert.Equal(new[] { sensor }, linked.RegainedGatewayConnectivitySensors);
        AssertPath(linked.Current, sensor, [sensor, MeshNodeKey.Gateway], 4);
        Assert.Single((await fixture.Store.GetSnapshotAsync()).Links);
    }

    [Fact]
    public async Task DecommissionedRelay_RemovesItsActualLinksAndPreventsStaleRoutes()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile bridgeSensor = await OnlineAsync(fixture, 12);
        SensorProfile leafSensor = await OnlineAsync(fixture, 13);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await AddAsync(fixture, 215, MeshNodeKey.Gateway, bridge, 1);
        await AddAsync(fixture, 216, bridge, leaf, 2);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        AssertPath(before, leaf, [leaf, bridge, MeshNodeKey.Gateway], 3);

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Decommissioned, 2);
        MeshSpanningTreeRefresh after =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.Empty((await fixture.Store.GetSnapshotAsync()).Links);
        Assert.Equal(new[] { bridge, leaf }.OrderBy(key => key).ToArray(),
            after.LostGatewayConnectivitySensors);
        Assert.True(after.Current.TryGetTreeRouteToGateway(bridge, out MeshTreeRoute? unavailable));
        Assert.NotNull(unavailable);
        Assert.Equal(MeshRouteStatus.Unavailable, unavailable.Status);
        AssertPath(before, leaf, [leaf, bridge, MeshNodeKey.Gateway], 3);
    }

    [Fact]
    public async Task WarningStatusAdvancesRevisionButDoesNotInvalidateWorkingRoute()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        SensorProfile profile = await OnlineAsync(fixture, 14);
        MeshNodeKey sensor = MeshTopologyScenario.Key(profile);
        await AddAsync(fixture, 217, MeshNodeKey.Gateway, sensor, 2);
        MeshGatewaySpanningTreeRouting before =
            await prim.GetGatewaySpanningTreeRoutingAsync();

        await fixture.SetStatusAsync(profile, SensorStatus.Warning, 2);
        MeshSpanningTreeRefresh after =
            await prim.RefreshGatewaySpanningTreeRoutingAsync(before);
        Assert.True(after.HasGraphRevisionChanged);
        Assert.False(after.HasBackboneChanged);
        Assert.Empty(after.RouteTransitions);
        AssertPath(after.Current, sensor, [sensor, MeshNodeKey.Gateway], 2);
        Assert.Single((await fixture.Store.GetSnapshotAsync()).Links);
    }

    [Fact]
    public async Task CancelledRefreshDoesNotPublishARecomputedTree()
    {
        var fixture = new MeshTopologyScenario();
        var prim = new MeshSpanningTreeService(fixture.Store);
        MeshGatewaySpanningTreeRouting previous =
            await prim.GetGatewaySpanningTreeRoutingAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prim.RefreshGatewaySpanningTreeRoutingAsync(previous, cancellation.Token));
        Assert.Equal(previous.GraphVersion,
            (await fixture.Store.GetSnapshotAsync()).Version);
    }

    private static async Task<SensorProfile> OnlineAsync(
        MeshTopologyScenario fixture,
        int ordinal)
    {
        SensorProfile sensor = await fixture.RegisterAsync(ordinal);
        await fixture.SetStatusAsync(sensor, SensorStatus.Online, 1);
        return sensor;
    }

    private static async Task AddAsync(
        MeshTopologyScenario fixture,
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double simulatedCost)
    {
        // Assert through the real mesh repository; no test-only edge writes.
        _ = await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(ordinal, first, second, simulatedCost));
    }

    private static void AssertPath(
        MeshGatewaySpanningTreeRouting projection,
        MeshNodeKey sensor,
        MeshNodeKey[] expectedPath,
        double expectedCost)
    {
        Assert.True(projection.TryGetTreeRouteToGateway(sensor, out MeshTreeRoute? route));
        Assert.NotNull(route);
        Assert.Equal(MeshRouteStatus.Reachable, route.Status);
        Assert.Equal(projection.GraphVersion, route.GraphVersion);
        Assert.Equal(expectedPath, route.PathToGateway);
        Assert.Equal(expectedPath.Length - 1, route.HopCount);
        Assert.Equal(expectedCost, route.AccumulatedSimulatedEnergyCost);
    }
}