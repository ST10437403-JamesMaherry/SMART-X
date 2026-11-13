using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-05 Commit 3: BFS regression through the real registered-sensor
/// repository and synchronized adjacency-list store. Each assertion is made
/// against a completed operation; no test assumes two independent snapshots
/// are atomic across concurrent graph modifications.
/// </summary>
public sealed class MeshBfsLiveTopologyRegressionTests
{
    [Fact]
    public async Task RegistrationAndActivation_DoNotInventGatewayConnections()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);

        MeshRoute? initially = await traversal.GetSensorRouteAsync(sensor.Id);
        Assert.NotNull(initially);
        Assert.Equal(MeshRouteStatus.Unavailable, initially.Status);

        await fixture.SetStatusAsync(sensor, SensorStatus.Online);
        MeshRoute? activated = await traversal.GetSensorRouteAsync(sensor.Id);
        Assert.NotNull(activated);
        Assert.Equal(MeshRouteStatus.Disconnected, activated.Status);
        Assert.Empty(activated.PathToGateway);
        Assert.Empty((await fixture.Store.GetSnapshotAsync()).Links);

        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(
            101, MeshNodeKey.Gateway, key));
        MeshRoute? connected = await traversal.GetSensorRouteAsync(sensor.Id);
        Assert.NotNull(connected);
        Assert.Equal(MeshRouteStatus.Reachable, connected.Status);
        Assert.Equal(new[] { key, MeshNodeKey.Gateway }, connected.PathToGateway);
        Assert.True(initially.GraphVersion < activated.GraphVersion);
        Assert.True(activated.GraphVersion < connected.GraphVersion);
    }

    [Fact]
    public async Task BridgeFailureAndRecovery_RefreshesPathsWithoutRemovingConfiguredLinks()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile bridgeSensor = await fixture.RegisterAsync(1);
        SensorProfile leafSensor = await fixture.RegisterAsync(2);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online, 1);
        await fixture.SetStatusAsync(leafSensor, SensorStatus.Online, 1);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(102, MeshNodeKey.Gateway, bridge));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(103, bridge, leaf));

        MeshRoute? before = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(before);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, before.PathToGateway);

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Offline, 2);
        MeshGatewayRouting during = await traversal.GetGatewayRoutingAsync();
        Assert.True(during.TryGetRouteToGateway(bridge, out MeshRoute? failedBridge));
        Assert.NotNull(failedBridge);
        Assert.Equal(MeshRouteStatus.Unavailable, failedBridge.Status);
        Assert.True(during.TryGetRouteToGateway(leaf, out MeshRoute? disconnectedLeaf));
        Assert.NotNull(disconnectedLeaf);
        Assert.Equal(MeshRouteStatus.Disconnected, disconnectedLeaf.Status);
        Assert.Empty(disconnectedLeaf.PathToGateway);
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online, 3);
        MeshRoute? recovered = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(recovered);
        Assert.Equal(MeshRouteStatus.Reachable, recovered.Status);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, recovered.PathToGateway);
        Assert.True(before.GraphVersion < during.GraphVersion);
        Assert.True(during.GraphVersion < recovered.GraphVersion);

        // Routes from earlier graph revisions must remain unchanged.
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, before.PathToGateway);
    }

    [Fact]
    public async Task RedundantLinks_RerouteAroundUnavailablePreferredBridge()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile first = await fixture.RegisterAsync(1);
        SensorProfile second = await fixture.RegisterAsync(2);
        SensorProfile leafSensor = await fixture.RegisterAsync(3);
        MeshNodeKey firstKey = MeshTopologyScenario.Key(first);
        MeshNodeKey secondKey = MeshTopologyScenario.Key(second);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);

        await fixture.SetStatusAsync(first, SensorStatus.Online);
        await fixture.SetStatusAsync(second, SensorStatus.Online);
        await fixture.SetStatusAsync(leafSensor, SensorStatus.Online);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(104, MeshNodeKey.Gateway, firstKey));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(105, MeshNodeKey.Gateway, secondKey));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(106, firstKey, leaf));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(107, secondKey, leaf));

        MeshNodeKey preferredKey = firstKey.CompareTo(secondKey) < 0 ? firstKey : secondKey;
        MeshNodeKey alternateKey = firstKey.CompareTo(secondKey) < 0 ? secondKey : firstKey;
        SensorProfile preferredSensor = preferredKey == firstKey ? first : second;

        MeshRoute? preferredRoute = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(preferredRoute);
        Assert.Equal(new[] { leaf, preferredKey, MeshNodeKey.Gateway },
            preferredRoute.PathToGateway);

        await fixture.SetStatusAsync(preferredSensor, SensorStatus.Fault, 2);
        MeshRoute? alternateRoute = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(alternateRoute);
        Assert.Equal(MeshRouteStatus.Reachable, alternateRoute.Status);
        Assert.Equal(new[] { leaf, alternateKey, MeshNodeKey.Gateway },
            alternateRoute.PathToGateway);
        Assert.Equal(4, (await fixture.Store.GetSnapshotAsync()).Links.Count);

        await fixture.SetStatusAsync(preferredSensor, SensorStatus.Online, 3);
        MeshRoute? restoredRoute = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(restoredRoute);
        Assert.Equal(new[] { leaf, preferredKey, MeshNodeKey.Gateway },
            restoredRoute.PathToGateway);
        Assert.True(preferredRoute.GraphVersion < alternateRoute.GraphVersion);
        Assert.True(alternateRoute.GraphVersion < restoredRoute.GraphVersion);
    }

    [Fact]
    public async Task RemovingTheOnlyGatewayBridge_ReturnsDisconnectedNotFalseSelfHealing()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile bridgeSensor = await fixture.RegisterAsync(1);
        SensorProfile leafSensor = await fixture.RegisterAsync(2);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online);
        await fixture.SetStatusAsync(leafSensor, SensorStatus.Online);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(108, MeshNodeKey.Gateway, bridge));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(109, bridge, leaf));

        MeshRoute? connected = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(connected);
        Assert.Equal(MeshRouteStatus.Reachable, connected.Status);

        Assert.True(await fixture.Store.RemoveLinkAsync(MeshTopologyScenario.LinkId(108)));
        MeshGatewayRouting broken = await traversal.GetGatewayRoutingAsync();
        Assert.Equal(new[] { bridge, leaf }.OrderBy(key => key).ToArray(),
            Assert.Single(broken.DisconnectedComponents).Sensors.ToArray());
        Assert.True(broken.TryGetRouteToGateway(leaf, out MeshRoute? unreachable));
        Assert.NotNull(unreachable);
        Assert.Equal(MeshRouteStatus.Disconnected, unreachable.Status);
        Assert.Empty(unreachable.PathToGateway);

        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(110, MeshNodeKey.Gateway, bridge));
        MeshRoute? restored = await traversal.GetSensorRouteAsync(leafSensor.Id);
        Assert.NotNull(restored);
        Assert.Equal(new[] { leaf, bridge, MeshNodeKey.Gateway }, restored.PathToGateway);
        Assert.True(connected.GraphVersion < broken.GraphVersion);
        Assert.True(broken.GraphVersion < restored.GraphVersion);
    }

    [Fact]
    public async Task EnergyCostMutation_ChangesVersionButNeverChangesHopRouting()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile directSensor = await fixture.RegisterAsync(1);
        SensorProfile bridgeSensor = await fixture.RegisterAsync(2);
        MeshNodeKey direct = MeshTopologyScenario.Key(directSensor);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        await fixture.SetStatusAsync(directSensor, SensorStatus.Online);
        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(111, MeshNodeKey.Gateway, direct, 500));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(112, MeshNodeKey.Gateway, bridge, 1));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(113, bridge, direct, 1));

        MeshRoute? before = await traversal.GetSensorRouteAsync(directSensor.Id);
        await fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(111), 1000);
        await fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(112), 0.1);
        MeshRoute? after = await traversal.GetSensorRouteAsync(directSensor.Id);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(new[] { direct, MeshNodeKey.Gateway }, before.PathToGateway);
        Assert.Equal(before.PathToGateway.ToArray(), after.PathToGateway.ToArray());
        Assert.Equal(before.HopCount, after.HopCount);
        Assert.Equal(before.GraphVersion + 2, after.GraphVersion);
    }

    [Fact]
    public async Task DecommissioningBridge_RemovesItsLinksAndLeavesDownstreamDisconnected()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile bridgeSensor = await fixture.RegisterAsync(1);
        SensorProfile leafSensor = await fixture.RegisterAsync(2);
        MeshNodeKey bridge = MeshTopologyScenario.Key(bridgeSensor);
        MeshNodeKey leaf = MeshTopologyScenario.Key(leafSensor);
        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Online);
        await fixture.SetStatusAsync(leafSensor, SensorStatus.Online);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(114, MeshNodeKey.Gateway, bridge));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(115, bridge, leaf));
        Assert.NotNull(await traversal.GetSensorRouteAsync(leafSensor.Id));

        await fixture.SetStatusAsync(bridgeSensor, SensorStatus.Decommissioned, 3);
        MeshGatewayRouting result = await traversal.GetGatewayRoutingAsync();
        MeshGraphSnapshot graph = await fixture.Store.GetSnapshotAsync();

        Assert.Empty(graph.Links);
        Assert.True(result.TryGetRouteToGateway(bridge, out MeshRoute? disabled));
        Assert.NotNull(disabled);
        Assert.Equal(MeshRouteStatus.Unavailable, disabled.Status);
        Assert.True(result.TryGetRouteToGateway(leaf, out MeshRoute? isolated));
        Assert.NotNull(isolated);
        Assert.Equal(MeshRouteStatus.Disconnected, isolated.Status);
        Assert.Equal(new[] { leaf }, Assert.Single(result.DisconnectedComponents).Sensors);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(116, bridge, leaf)));
    }

    [Fact]
    public async Task LaterRegisteredSensor_DoesNotInheritAnUnrelatedGatewayPath()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile first = await fixture.RegisterAsync(1);
        await fixture.SetStatusAsync(first, SensorStatus.Online);
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(
            117, MeshNodeKey.Gateway, MeshTopologyScenario.Key(first)));
        MeshRoute? existing = await traversal.GetSensorRouteAsync(first.Id);
        Assert.NotNull(existing);
        Assert.Equal(MeshRouteStatus.Reachable, existing.Status);

        SensorProfile later = await fixture.RegisterAsync(2);
        await fixture.SetStatusAsync(later, SensorStatus.Online);
        MeshRoute? isolated = await traversal.GetSensorRouteAsync(later.Id);
        Assert.NotNull(isolated);
        Assert.Equal(MeshRouteStatus.Disconnected, isolated.Status);
        Assert.Empty(isolated.PathToGateway);
        Assert.Single((await fixture.Store.GetSnapshotAsync()).Links);
        Assert.Null(await traversal.GetSensorRouteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ConcurrentLinkChurn_ReadersReturnOnlyValidSingleRevisionRoutes()
    {
        var fixture = new MeshTopologyScenario();
        var traversal = new MeshTraversalService(fixture.Store);
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);
        await fixture.SetStatusAsync(sensor, SensorStatus.Online);
        const int iterations = 48;

        Task writer = Task.Run(async () =>
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Guid id = MeshTopologyScenario.LinkId(1000 + iteration);
                await fixture.Store.AddLinkAsync(new MeshLink(id,
                    MeshNodeKey.Gateway, key, 1));
                await Task.Yield();
                Assert.True(await fixture.Store.RemoveLinkAsync(id));
            }
        });

        Task reader = Task.Run(async () =>
        {
            long lastObservedVersion = -1;
            for (int iteration = 0; iteration < iterations * 2; iteration++)
            {
                MeshGatewayRouting routing = await traversal.GetGatewayRoutingAsync();
                Assert.True(routing.GraphVersion >= lastObservedVersion);
                lastObservedVersion = routing.GraphVersion;
                Assert.True(routing.TryGetRouteToGateway(key, out MeshRoute? route));
                Assert.NotNull(route);
                Assert.Equal(routing.GraphVersion, route.GraphVersion);

                if (route.Status == MeshRouteStatus.Reachable)
                {
                    Assert.Equal(new[] { key, MeshNodeKey.Gateway },
                        route.PathToGateway);
                    Assert.Equal(1, route.HopCount);
                    Assert.Empty(routing.DisconnectedComponents);
                }
                else
                {
                    Assert.Equal(MeshRouteStatus.Disconnected, route.Status);
                    Assert.Null(route.HopCount);
                    Assert.Empty(route.PathToGateway);
                    Assert.Equal(new[] { key },
                        Assert.Single(routing.DisconnectedComponents).Sensors);
                }

                await Task.Yield();
            }
        });

        await Task.WhenAll(writer, reader);
        MeshGatewayRouting after = await traversal.GetGatewayRoutingAsync();
        Assert.Equal(new[] { key },
            Assert.Single(after.DisconnectedComponents).Sensors);
        Assert.Empty((await fixture.Store.GetSnapshotAsync()).Links);
    }
}