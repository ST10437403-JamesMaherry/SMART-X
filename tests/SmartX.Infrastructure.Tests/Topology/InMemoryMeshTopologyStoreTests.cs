using SmartX.Application.Sensors;
using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-04 Commit 2: verifies the real adjacency-list store and its integration
/// with the authoritative sensor registry. Large parallel stress and graph
/// invariant regression fixtures are deliberately reserved for Commit 3.
/// </summary>
public sealed class InMemoryMeshTopologyStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid FirstLinkId =
        Guid.Parse("00000000-0000-0000-0000-000000000101");

    private static readonly Guid SecondLinkId =
        Guid.Parse("00000000-0000-0000-0000-000000000102");

    private static readonly Guid ThirdLinkId =
        Guid.Parse("00000000-0000-0000-0000-000000000103");

    [Fact]
    public async Task EmptyRegistry_HasOneLogicalGatewayAndNoLinks()
    {
        var store = CreateStore(out _);

        MeshGraphSnapshot first = await store.GetSnapshotAsync();
        MeshGraphSnapshot second = await store.GetSnapshotAsync();

        Assert.Equal(0L, first.Version);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(MeshNodeKey.Gateway, Assert.Single(first.Nodes).Key);
        Assert.Empty(first.Links);
        Assert.Empty(await store.GetIncidentLinksAsync(MeshNodeKey.Gateway));
    }

    [Fact]
    public async Task SensorRegistration_ProducesAnIsolatedVertexNotAFabricatedLink()
    {
        var store = CreateStore(out ISensorRepository registry);
        MeshGraphSnapshot before = await store.GetSnapshotAsync();
        SensorProfile sensor = await RegisterAsync(registry, 1);

        MeshGraphSnapshot after = await store.GetSnapshotAsync();

        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(2, after.Nodes.Count);
        Assert.Equal(MeshNodeKey.Gateway, after.Nodes[0].Key);
        Assert.Contains(after.Nodes, node =>
            node.Key == MeshNodeKey.ForSensor(sensor.Id) &&
            node.Status == SensorStatus.Offline);
        Assert.Empty(after.Links);
        Assert.Empty(await store.GetIncidentLinksAsync(
            MeshNodeKey.ForSensor(sensor.Id)));
    }

    [Fact]
    public async Task AddLink_UsesTheSameObjectInBothAdjacencyDirections()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 2);
        MeshNodeKey sensorKey = MeshNodeKey.ForSensor(sensor.Id);
        var link = new MeshLink(FirstLinkId, sensorKey, MeshNodeKey.Gateway, 2.25);

        MeshLink stored = await store.AddLinkAsync(link);
        MeshGraphSnapshot snapshot = await store.GetSnapshotAsync();
        IReadOnlyList<MeshLink> gatewayLinks =
            await store.GetIncidentLinksAsync(MeshNodeKey.Gateway);
        IReadOnlyList<MeshLink> sensorLinks =
            await store.GetIncidentLinksAsync(sensorKey);

        Assert.Same(link, stored);
        Assert.Same(link, Assert.Single(gatewayLinks));
        Assert.Same(link, Assert.Single(sensorLinks));
        Assert.Same(link, Assert.Single(snapshot.Links));
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Links);
    }

    [Fact]
    public async Task DuplicateUndirectedPair_IsRejectedWithoutAnExtraEdgeOrVersion()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 3);
        MeshNodeKey key = MeshNodeKey.ForSensor(sensor.Id);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, key, 1));
        long priorVersion = (await store.GetSnapshotAsync()).Version;

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.AddLinkAsync(new MeshLink(
                SecondLinkId, key, MeshNodeKey.Gateway, 9)));

        MeshGraphSnapshot current = await store.GetSnapshotAsync();
        Assert.Equal(priorVersion, current.Version);
        Assert.Equal(FirstLinkId, Assert.Single(current.Links).Id);
    }

    [Fact]
    public async Task UnknownSensorEndpoint_CannotBeLinkedOrQueried()
    {
        var store = CreateStore(out _);
        MeshNodeKey unknown = MeshNodeKey.ForSensor(Guid.NewGuid());
        var invalidLink = new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, unknown, 1);

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.AddLinkAsync(invalidLink));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.GetIncidentLinksAsync(unknown));

        Assert.Empty((await store.GetSnapshotAsync()).Links);
    }

    [Fact]
    public async Task RemoveLink_RemovesBothDirectionsAndIsIdempotent()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 4);
        MeshNodeKey key = MeshNodeKey.ForSensor(sensor.Id);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, key, 3));

        Assert.True(await store.RemoveLinkAsync(FirstLinkId));
        MeshGraphSnapshot removed = await store.GetSnapshotAsync();
        Assert.False(await store.RemoveLinkAsync(FirstLinkId));

        Assert.Empty(removed.Links);
        Assert.Empty(await store.GetIncidentLinksAsync(MeshNodeKey.Gateway));
        Assert.Empty(await store.GetIncidentLinksAsync(key));
        Assert.Equal(2, removed.Nodes.Count);
        Assert.Equal(removed.Version, (await store.GetSnapshotAsync()).Version);
    }

    [Fact]
    public async Task UpdateCost_PreservesIdentityAndOldImmutableSnapshot()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 5);
        MeshNodeKey key = MeshNodeKey.ForSensor(sensor.Id);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, key, 5));
        MeshGraphSnapshot before = await store.GetSnapshotAsync();

        MeshLink updated = await store.UpdateLinkCostAsync(FirstLinkId, 1.5);
        MeshGraphSnapshot after = await store.GetSnapshotAsync();

        Assert.Equal(FirstLinkId, updated.Id);
        Assert.Equal(5d, Assert.Single(before.Links).SimulatedEnergyCost);
        Assert.Equal(1.5d, Assert.Single(after.Links).SimulatedEnergyCost);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Same(updated, Assert.Single(
            await store.GetIncidentLinksAsync(key)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshLink>)before.Links).Clear());
    }

    [Fact]
    public async Task NoOpCostUpdate_DoesNotBumpVersion()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 6);
        var link = new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(sensor.Id), 2);
        await store.AddLinkAsync(link);
        long version = (await store.GetSnapshotAsync()).Version;

        MeshLink unchanged = await store.UpdateLinkCostAsync(FirstLinkId, 2);

        Assert.Same(link, unchanged);
        Assert.Equal(version, (await store.GetSnapshotAsync()).Version);
    }

    [Fact]
    public async Task SnapshotAndNeighbourOrdering_IsDeterministicRegardlessOfInsertOrder()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile first = await RegisterAsync(registry, 7);
        SensorProfile second = await RegisterAsync(registry, 8);
        MeshNodeKey firstKey = MeshNodeKey.ForSensor(first.Id);
        MeshNodeKey secondKey = MeshNodeKey.ForSensor(second.Id);

        var links = new[]
        {
            new MeshLink(ThirdLinkId, firstKey, secondKey, 7),
            new MeshLink(SecondLinkId, MeshNodeKey.Gateway, secondKey, 2),
            new MeshLink(FirstLinkId, MeshNodeKey.Gateway, firstKey, 1)
        };

        foreach (MeshLink link in links)
        {
            await store.AddLinkAsync(link);
        }

        MeshGraphSnapshot snapshot = await store.GetSnapshotAsync();
        MeshNodeKey[] expectedNodes =
            new[] { MeshNodeKey.Gateway, firstKey, secondKey }
                .OrderBy(key => key).ToArray();
        MeshLink[] expectedLinks = links
            .OrderBy(link => link.NodeA)
            .ThenBy(link => link.NodeB)
            .ThenBy(link => link.Id).ToArray();
        MeshLink[] expectedGatewayLinks = links
            .Where(link => link.Contains(MeshNodeKey.Gateway))
            .OrderBy(link => link.OtherEndpoint(MeshNodeKey.Gateway))
            .ToArray();

        Assert.Equal(expectedNodes, snapshot.Nodes.Select(node => node.Key));
        Assert.Equal(expectedLinks, snapshot.Links);
        Assert.Equal(expectedGatewayLinks,
            await store.GetIncidentLinksAsync(MeshNodeKey.Gateway));
        Assert.Equal(snapshot.Version, (await store.GetSnapshotAsync()).Version);
    }

    [Fact]
    public async Task OfflineSensor_RemainsInventoriedAndLinkedButNotForwarding()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 9);
        MeshNodeKey key = MeshNodeKey.ForSensor(sensor.Id);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, key, 1));
        MeshGraphSnapshot offline = await store.GetSnapshotAsync();

        Assert.False(offline.Nodes.Single(node => node.Key == key)
            .IsForwardingEligible);
        Assert.Single(offline.Links);

        await SetStatusAsync(registry, sensor, SensorStatus.Online, 1);
        MeshGraphSnapshot online = await store.GetSnapshotAsync();
        Assert.True(online.Nodes.Single(node => node.Key == key)
            .IsForwardingEligible);
        Assert.False(offline.Nodes.Single(node => node.Key == key)
            .IsForwardingEligible);
        Assert.Single(online.Links);
        Assert.True(online.Version > offline.Version);
    }

    [Fact]
    public async Task DecommissionedSensor_KeepsInventoryButDetachesAllLinks()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 10);
        MeshNodeKey key = MeshNodeKey.ForSensor(sensor.Id);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway, key, 4));
        MeshGraphSnapshot before = await store.GetSnapshotAsync();

        await SetStatusAsync(registry, sensor, SensorStatus.Decommissioned, 1);
        MeshGraphSnapshot after = await store.GetSnapshotAsync();

        Assert.Single(before.Links);
        Assert.Empty(after.Links);
        Assert.Empty(await store.GetIncidentLinksAsync(MeshNodeKey.Gateway));
        Assert.Equal(SensorStatus.Decommissioned,
            after.Nodes.Single(node => node.Key == key).Status);
        Assert.False(after.Nodes.Single(node => node.Key == key)
            .IsForwardingEligible);
        Assert.True(after.Version > before.Version);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.AddLinkAsync(new MeshLink(
                SecondLinkId, MeshNodeKey.Gateway, key, 2)));
    }

    [Fact]
    public async Task CancelledRead_DoesNotChangeGraphVersion()
    {
        var store = CreateStore(out ISensorRepository registry);
        await RegisterAsync(registry, 11);
        long priorVersion = (await store.GetSnapshotAsync()).Version;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GetSnapshotAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.RemoveLinkAsync(FirstLinkId, cancelled.Token));

        Assert.Equal(priorVersion, (await store.GetSnapshotAsync()).Version);
    }

    [Fact]
    public async Task InvalidUpdateOrRemoval_DoesNotChangeExistingLinks()
    {
        var store = CreateStore(out ISensorRepository registry);
        SensorProfile sensor = await RegisterAsync(registry, 12);
        await store.AddLinkAsync(new MeshLink(
            FirstLinkId, MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(sensor.Id), 5));
        long version = (await store.GetSnapshotAsync()).Version;

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.UpdateLinkCostAsync(FirstLinkId, double.NaN));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.UpdateLinkCostAsync(SecondLinkId, 2));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.RemoveLinkAsync(Guid.Empty));

        MeshGraphSnapshot snapshot = await store.GetSnapshotAsync();
        Assert.Equal(version, snapshot.Version);
        Assert.Equal(5d, Assert.Single(snapshot.Links).SimulatedEnergyCost);
    }

    private static IMeshTopologyStore CreateStore(out ISensorRepository registry)
    {
        registry = new InMemorySensorRepository();
        return new InMemoryMeshTopologyStore(registry);
    }

    private static async Task<SensorProfile> RegisterAsync(
        ISensorRepository registry,
        byte ordinal)
    {
        SensorProfile profile = SensorProfile.Create(
            $"Mesh sensor {ordinal}",
            DeviceIdentifier.FromMacAddress($"AA:BB:CC:DD:EE:{ordinal:X2}"),
            new DeploymentLocation("Lab", "Mesh", $"Node-{ordinal}"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            CreatedAt);

        Assert.True(await registry.TryAddAsync(profile));
        return profile;
    }

    private static async Task SetStatusAsync(
        ISensorRepository registry,
        SensorProfile sensor,
        SensorStatus status,
        int minuteOffset)
    {
        sensor.ChangeStatus(status, CreatedAt.AddMinutes(minuteOffset));
        Assert.True(await registry.UpdateAsync(sensor));
    }
}