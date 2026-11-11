using SmartX.Application.Sensors;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;
using SmartX.Infrastructure.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-04 Commit 3: adversarial graph fixtures. These exercise the real
/// adjacency-list boundary, including impossible physical links, isolated
/// vertices, authoritative registry changes and rollback after validation.
/// BFS, route metrics and Prim MST remain intentionally out of scope.
/// </summary>
public sealed class MeshTopologyAdversarialTests
{
    [Fact]
    public async Task DisconnectedComponents_KeepGatewayAndIsolatedSensorSeparate()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile[] sensors = await RegisterManyAsync(fixture, 5);
        MeshNodeKey[] keys = sensors.Select(MeshTopologyScenario.Key).ToArray();

        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(1, keys[0], keys[1], 2));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(2, keys[2], keys[3], 3));

        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        MeshTopologyAssertions.AssertCoherent(snapshot);
        Assert.Equal(6, snapshot.Nodes.Count);
        Assert.Equal(2, snapshot.Links.Count);
        Assert.Empty(await fixture.Store.GetIncidentLinksAsync(MeshNodeKey.Gateway));
        Assert.Empty(await fixture.Store.GetIncidentLinksAsync(keys[4]));
        Assert.Single(await fixture.Store.GetIncidentLinksAsync(keys[0]));
        Assert.Single(await fixture.Store.GetIncidentLinksAsync(keys[3]));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task DenseUndirectedGraph_HasExactlyOneEdgePerUnorderedPair()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile[] sensors = await RegisterManyAsync(fixture, 9);
        MeshNodeKey[] keys = new[] { MeshNodeKey.Gateway }
            .Concat(sensors.Select(MeshTopologyScenario.Key))
            .ToArray();
        var links = new List<MeshLink>();
        int ordinal = 1;

        for (int first = 0; first < keys.Length; first++)
        {
            for (int second = first + 1; second < keys.Length; second++)
            {
                links.Add(MeshTopologyScenario.Link(
                    ordinal++, keys[first], keys[second], 0.5d + ordinal));
            }
        }

        // Reverse insertion order to show that iteration order is NOT the
        // graph's observable order or a surrogate for neighbor ordering.
        foreach (MeshLink link in links.AsEnumerable().Reverse())
        {
            await fixture.Store.AddLinkAsync(link);
        }

        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        MeshTopologyAssertions.AssertCoherent(snapshot);

        // For ten vertices, an undirected complete graph has 10 * 9 / 2 =
        // 45 unique links, not 55. The nine registered sensors each add one
        // inventory revision and every inserted link adds one more revision.
        int expectedLinks = keys.Length * (keys.Length - 1) / 2;
        Assert.Equal(45, expectedLinks);
        Assert.Equal(expectedLinks, snapshot.Links.Count);
        Assert.Equal(10, snapshot.Nodes.Count);
        Assert.Equal(sensors.Length + (long)expectedLinks, snapshot.Version);

        foreach (MeshNodeKey key in keys)
        {
            Assert.Equal(9, (await fixture.Store.GetIncidentLinksAsync(key)).Count);
        }

        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ReusedLinkIdForDifferentEndpoints_IsRejectedAtomically()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile[] sensors = await RegisterManyAsync(fixture, 2);
        MeshNodeKey first = MeshTopologyScenario.Key(sensors[0]);
        MeshNodeKey second = MeshTopologyScenario.Key(sensors[1]);
        await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(10, MeshNodeKey.Gateway, first));
        long version = (await fixture.Store.GetSnapshotAsync()).Version;

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.AddLinkAsync(
                MeshTopologyScenario.Link(10, MeshNodeKey.Gateway, second, 5)));

        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(version, snapshot.Version);
        Assert.Equal(first, Assert.Single(snapshot.Links)
            .OtherEndpoint(MeshNodeKey.Gateway));
        Assert.Empty(await fixture.Store.GetIncidentLinksAsync(second));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task RemovingThenRecreatingMirroredEdge_ClearsBothOldIndexes()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);
        await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(20, MeshNodeKey.Gateway, key, 2));
        long version = (await fixture.Store.GetSnapshotAsync()).Version;

        Assert.True(await fixture.Store.RemoveLinkAsync(MeshTopologyScenario.LinkId(20)));
        Assert.False(await fixture.Store.RemoveLinkAsync(MeshTopologyScenario.LinkId(20)));
        await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(21, key, MeshNodeKey.Gateway, 7));

        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(version + 2, snapshot.Version);
        Assert.Equal(MeshTopologyScenario.LinkId(21), Assert.Single(snapshot.Links).Id);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(20), 3));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task DecommissionedHighDegreeSensor_LosesOnlyItsIncidentEdges()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile[] sensors = await RegisterManyAsync(fixture, 5);
        MeshNodeKey[] keys = sensors.Select(MeshTopologyScenario.Key).ToArray();
        MeshNodeKey hub = keys[0];

        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(31, hub, MeshNodeKey.Gateway));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(32, hub, keys[1]));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(33, hub, keys[2]));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(34, hub, keys[3]));
        await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(35, keys[1], keys[4]));

        MeshGraphSnapshot before = await fixture.Store.GetSnapshotAsync();
        await fixture.SetStatusAsync(sensors[0], SensorStatus.Decommissioned);
        MeshGraphSnapshot after = await fixture.Store.GetSnapshotAsync();

        Assert.Equal(5, before.Links.Count);
        Assert.Equal(MeshTopologyScenario.LinkId(35), Assert.Single(after.Links).Id);
        Assert.Empty(await fixture.Store.GetIncidentLinksAsync(hub));
        Assert.Equal(SensorStatus.Decommissioned,
            after.Nodes.Single(node => node.Key == hub).Status);
        Assert.Equal(before.Version + 5, after.Version);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.AddLinkAsync(
                MeshTopologyScenario.Link(36, hub, MeshNodeKey.Gateway)));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Theory]
    [InlineData(SensorStatus.Warning, true)]
    [InlineData(SensorStatus.Fault, false)]
    [InlineData(SensorStatus.Maintenance, false)]
    [InlineData(SensorStatus.Offline, false)]
    public async Task SensorHealthChanges_KeepSimulatedLinkButUpdateForwardingEligibility(
        SensorStatus status,
        bool eligible)
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);
        await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(40, MeshNodeKey.Gateway, key));
        await fixture.SetStatusAsync(sensor, SensorStatus.Online, 1);
        MeshGraphSnapshot online = await fixture.Store.GetSnapshotAsync();
        await fixture.SetStatusAsync(sensor, status, 2);
        MeshGraphSnapshot changed = await fixture.Store.GetSnapshotAsync();

        Assert.True(online.Nodes.Single(node => node.Key == key).IsForwardingEligible);
        Assert.Equal(eligible,
            changed.Nodes.Single(node => node.Key == key).IsForwardingEligible);
        Assert.Single(changed.Links);
        Assert.Single(online.Links);
        Assert.Equal(online.Version + 1, changed.Version);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.125d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InvalidCostUpdates_CannotPartiallyReplaceAdjacency(
        double badCost)
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);
        await fixture.Store.AddLinkAsync(
            MeshTopologyScenario.Link(50, MeshNodeKey.Gateway, key, 2));
        MeshGraphSnapshot before = await fixture.Store.GetSnapshotAsync();

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(50), badCost));

        MeshGraphSnapshot after = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Same(Assert.Single(before.Links), Assert.Single(after.Links));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task InvalidNodeKeysAndUnknownLinkIds_AreRejectedWithoutMutation()
    {
        var fixture = new MeshTopologyScenario();
        MeshGraphSnapshot before = await fixture.Store.GetSnapshotAsync();
        MeshNodeKey absent = MeshNodeKey.ForSensor(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.GetIncidentLinksAsync(default));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.GetIncidentLinksAsync(absent));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.RemoveLinkAsync(Guid.Empty));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.UpdateLinkCostAsync(Guid.Empty, 2));
        Assert.False(await fixture.Store.RemoveLinkAsync(MeshTopologyScenario.LinkId(999)));
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            fixture.Store.UpdateLinkCostAsync(MeshTopologyScenario.LinkId(999), 2));
        Assert.Equal(before.Version, (await fixture.Store.GetSnapshotAsync()).Version);
    }

    [Fact]
    public async Task MutableRegistryRemoval_ClearsDanglingLinksAndReintroductionIsIsolated()
    {
        SensorProfile first = MeshTopologyScenario.NewProfile(1);
        SensorProfile second = MeshTopologyScenario.NewProfile(2);
        var inventory = new ScriptedSensorInventory(first, second);
        var store = new InMemoryMeshTopologyStore(inventory);
        MeshNodeKey firstKey = MeshTopologyScenario.Key(first);
        MeshNodeKey secondKey = MeshTopologyScenario.Key(second);

        await store.AddLinkAsync(
            MeshTopologyScenario.Link(60, MeshNodeKey.Gateway, firstKey));
        await store.AddLinkAsync(
            MeshTopologyScenario.Link(61, firstKey, secondKey));
        MeshGraphSnapshot original = await store.GetSnapshotAsync();

        inventory.Set(second);
        MeshGraphSnapshot removed = await store.GetSnapshotAsync();
        Assert.Equal(2, removed.Nodes.Count); // Gateway and second sensor.
        Assert.Empty(removed.Links);
        Assert.DoesNotContain(removed.Nodes, node => node.Key == firstKey);
        Assert.Empty(await store.GetIncidentLinksAsync(secondKey));
        Assert.True(removed.Version > original.Version);

        inventory.Set(first, second);
        MeshGraphSnapshot restored = await store.GetSnapshotAsync();
        Assert.Equal(3, restored.Nodes.Count);
        Assert.Empty(restored.Links); // Restoration must NOT invent radio links.
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(store);
    }

    [Fact]
    public async Task DuplicateRegistryProfiles_AreRejectedBeforeAnyPartialProjection()
    {
        SensorProfile first = MeshTopologyScenario.NewProfile(1);
        SensorProfile second = MeshTopologyScenario.NewProfile(2);
        var inventory = new ScriptedSensorInventory(first);
        var store = new InMemoryMeshTopologyStore(inventory);
        MeshGraphSnapshot baseline = await store.GetSnapshotAsync();

        // An intentionally malformed test registry must not publish the
        // second vertex before discovering its duplicate profile identity.
        inventory.Set(first, second, second);
        await Assert.ThrowsAsync<InvalidMeshTopologyException>(() =>
            store.GetSnapshotAsync());

        inventory.Set(first);
        MeshGraphSnapshot recovered = await store.GetSnapshotAsync();
        Assert.Equal(baseline.Version, recovered.Version);
        Assert.Equal(baseline.Nodes.Select(node => node.Key),
            recovered.Nodes.Select(node => node.Key));
        Assert.Empty(recovered.Links);
    }

    [Fact]
    public async Task RegistryReadFailure_DoesNotCorruptMeshAndLaterCallsRecover()
    {
        SensorProfile first = MeshTopologyScenario.NewProfile(1);
        var inventory = new ScriptedSensorInventory(first);
        var store = new InMemoryMeshTopologyStore(inventory);
        await store.AddLinkAsync(
            MeshTopologyScenario.Link(70, MeshNodeKey.Gateway,
                MeshTopologyScenario.Key(first)));
        MeshGraphSnapshot before = await store.GetSnapshotAsync();
        inventory.Failure = new InvalidOperationException("Injected registry outage.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetSnapshotAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RemoveLinkAsync(MeshTopologyScenario.LinkId(70)));

        inventory.Failure = null;
        MeshGraphSnapshot after = await store.GetSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Same(Assert.Single(before.Links), Assert.Single(after.Links));
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(store);
    }

    private static async Task<SensorProfile[]> RegisterManyAsync(
        MeshTopologyScenario fixture,
        int count)
    {
        var sensors = new List<SensorProfile>(count);
        for (int ordinal = 1; ordinal <= count; ordinal++)
        {
            sensors.Add(await fixture.RegisterAsync(ordinal));
        }

        return sensors.ToArray();
    }

    /// <summary>
    /// Tests a future registry-deletion path and malformed inventories without
    /// adding unsupported delete APIs to the production sensor repository.
    /// The mesh invokes only GetAllAsync on this substitute.
    /// </summary>
    private sealed class ScriptedSensorInventory : ISensorRepository
    {
        private IReadOnlyList<SensorProfile> _sensors;

        public ScriptedSensorInventory(params SensorProfile[] sensors)
        {
            _sensors = sensors;
        }

        public Exception? Failure { get; set; }

        public void Set(params SensorProfile[] sensors) => _sensors = sensors;

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(_sensors);
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}