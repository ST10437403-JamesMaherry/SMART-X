using SmartX.Application.Sensors;
using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// Deterministic fixtures for P3-04 graph regression tests. This deliberately
/// uses the production sensor repository and mesh store rather than replacing
/// their synchronization or projecting fictitious API-only sensor identities.
/// </summary>
internal sealed class MeshTopologyScenario
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    public ISensorRepository Registry { get; } = new InMemorySensorRepository();

    public IMeshTopologyStore Store { get; }

    public MeshTopologyScenario()
    {
        Store = new InMemoryMeshTopologyStore(Registry);
    }

    public async Task<SensorProfile> RegisterAsync(int ordinal)
    {
        SensorProfile sensor = NewProfile(ordinal);
        Assert.True(await Registry.TryAddAsync(sensor));
        return sensor;
    }

    public static SensorProfile NewProfile(int ordinal)
    {
        if (ordinal is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        // Unique, well-formed MAC addresses, including test cases beyond the
        // one-byte suffix used by the smaller Commit 2 fixtures.
        string mac = $"AA:BB:CC:DD:{(ordinal >> 8):X2}:{(ordinal & 0xFF):X2}";
        return SensorProfile.Create(
            $"Mesh regression sensor {ordinal}",
            DeviceIdentifier.FromMacAddress(mac),
            new DeploymentLocation("Test facility", "Mesh lab", $"Node-{ordinal}"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            Epoch);
    }

    public async Task SetStatusAsync(
        SensorProfile sensor,
        SensorStatus status,
        int minuteOffset = 1)
    {
        sensor.ChangeStatus(status, Epoch.AddMinutes(minuteOffset));
        Assert.True(await Registry.UpdateAsync(sensor));
    }

    public static MeshNodeKey Key(SensorProfile sensor) =>
        MeshNodeKey.ForSensor(sensor.Id);

    public static Guid LinkId(int ordinal)
    {
        if (ordinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return Guid.Parse($"00000000-0000-0000-0000-{ordinal:X12}");
    }

    public static MeshLink Link(
        int ordinal,
        MeshNodeKey from,
        MeshNodeKey to,
        double cost = 1d) =>
        new(LinkId(ordinal), from, to, cost);
}

/// <summary>
/// Independent graph-oracle checks. These intentionally reconstruct adjacency
/// from the public undirected edge list instead of reading store internals.
/// All checks on live concurrent snapshots use only ONE immutable revision.
/// </summary>
internal static class MeshTopologyAssertions
{
    public static void AssertCoherent(MeshGraphSnapshot snapshot)
    {
        Assert.True(snapshot.Version >= 0);

        MeshNodeKey[] keys = snapshot.Nodes.Select(node => node.Key).ToArray();
        Assert.Equal(keys.OrderBy(key => key).ToArray(), keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(MeshNodeKey.Gateway, Assert.Single(
            snapshot.Nodes, node => node.IsGateway).Key);

        var keySet = keys.ToHashSet();
        MeshLink[] expectedOrder = snapshot.Links
            .OrderBy(link => link.NodeA)
            .ThenBy(link => link.NodeB)
            .ThenBy(link => link.Id)
            .ToArray();

        Assert.Equal(expectedOrder, snapshot.Links.ToArray());
        Assert.Equal(snapshot.Links.Count,
            snapshot.Links.Select(link => link.Id).Distinct().Count());
        Assert.Equal(snapshot.Links.Count,
            snapshot.Links.Select(link => (link.NodeA, link.NodeB))
                .Distinct().Count());

        foreach (MeshLink link in snapshot.Links)
        {
            Assert.Contains(link.NodeA, keySet);
            Assert.Contains(link.NodeB, keySet);
            Assert.True(link.NodeA.CompareTo(link.NodeB) < 0);
            Assert.True(double.IsFinite(link.SimulatedEnergyCost));
            Assert.True(link.SimulatedEnergyCost > 0d);
            Assert.NotEqual(SensorStatus.Decommissioned,
                snapshot.Nodes.Single(node => node.Key == link.NodeA).Status);
            Assert.NotEqual(SensorStatus.Decommissioned,
                snapshot.Nodes.Single(node => node.Key == link.NodeB).Status);
        }
    }

    /// <summary>
    /// Compare both adjacency directions with a single stable edge list.
    /// Call only when the writer tasks have finished: individual public reads
    /// legitimately may refer to different revisions under concurrent writes.
    /// </summary>
    public static async Task AssertAdjacencyMatchesSnapshotAsync(
        IMeshTopologyStore store)
    {
        MeshGraphSnapshot snapshot = await store.GetSnapshotAsync();
        AssertCoherent(snapshot);

        foreach (MeshNode node in snapshot.Nodes)
        {
            MeshLink[] expected = snapshot.Links
                .Where(link => link.Contains(node.Key))
                .OrderBy(link => link.OtherEndpoint(node.Key))
                .ToArray();
            IReadOnlyList<MeshLink> incident =
                await store.GetIncidentLinksAsync(node.Key);

            Assert.Equal(expected, incident.ToArray());
            Assert.Throws<NotSupportedException>(() =>
                ((IList<MeshLink>)incident).Clear());
        }

        Assert.Equal(snapshot.Version, (await store.GetSnapshotAsync()).Version);
    }
}