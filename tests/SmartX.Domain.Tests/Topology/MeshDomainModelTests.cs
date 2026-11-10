using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-04: domain invariants for the graph model. These tests deliberately
/// exclude BFS/MST and persistence, which belong to subsequent commits/issues.
/// </summary>
public sealed class MeshDomainModelTests
{
    private static readonly Guid SensorOneId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly Guid SensorTwoId =
        Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly Guid SensorThreeId =
        Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static readonly Guid LinkOneId =
        Guid.Parse("00000000-0000-0000-0000-000000000101");

    private static readonly Guid LinkTwoId =
        Guid.Parse("00000000-0000-0000-0000-000000000102");

    [Fact]
    public void Gateway_HasStableIdentity_AndCannotBeConfusedWithASensor()
    {
        MeshNodeKey gateway = MeshNodeKey.Gateway;
        MeshNodeKey sensor = MeshNodeKey.ForSensor(SensorOneId);

        Assert.True(gateway.IsGateway);
        Assert.True(gateway.IsValid);
        Assert.Equal(Guid.Empty, gateway.SensorProfileId);
        Assert.Equal("gateway", gateway.ToString());
        Assert.False(sensor.IsGateway);
        Assert.Equal(SensorOneId, sensor.SensorProfileId);
        Assert.NotEqual(gateway, sensor);
        Assert.NotEqual(MeshNodeKey.Gateway, default(MeshNodeKey));
        Assert.False(default(MeshNodeKey).IsValid);
    }

    [Fact]
    public void SensorKey_RejectsEmptyIdentity()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            MeshNodeKey.ForSensor(Guid.Empty));
    }

    [Fact]
    public void NodeOrdering_IsGatewayFirstAndThenStableSensorIdentifierOrder()
    {
        var keys = new[]
        {
            MeshNodeKey.ForSensor(SensorThreeId),
            MeshNodeKey.ForSensor(SensorOneId),
            MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(SensorTwoId)
        };

        Assert.Equal(
            new[]
            {
                MeshNodeKey.Gateway,
                MeshNodeKey.ForSensor(SensorOneId),
                MeshNodeKey.ForSensor(SensorTwoId),
                MeshNodeKey.ForSensor(SensorThreeId)
            },
            keys.OrderBy(key => key).ToArray());
    }

    [Fact]
    public void GatewayNode_CannotCarrySensorStatus()
    {
        Assert.Null(MeshNode.Gateway.Status);
        Assert.True(MeshNode.Gateway.IsForwardingEligible);
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNode(MeshNodeKey.Gateway, SensorStatus.Online));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            MeshNode.Gateway.WithStatus(SensorStatus.Online));
    }

    [Fact]
    public void SensorNode_RequiresDefinedStatusAndValidIdentity()
    {
        MeshNodeKey key = MeshNodeKey.ForSensor(SensorOneId);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNode(key, null));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNode(default, SensorStatus.Online));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNode(key, (SensorStatus)999));
    }

    [Theory]
    [InlineData(SensorStatus.Online, true)]
    [InlineData(SensorStatus.Warning, true)]
    [InlineData(SensorStatus.Offline, false)]
    [InlineData(SensorStatus.Fault, false)]
    [InlineData(SensorStatus.Maintenance, false)]
    [InlineData(SensorStatus.Decommissioned, false)]
    public void ForwardingEligibility_ReflectsRealSensorStatus(
        SensorStatus status,
        bool expected)
    {
        var node = new MeshNode(MeshNodeKey.ForSensor(SensorOneId), status);
        Assert.Equal(expected, node.IsForwardingEligible);
        Assert.Equal(status, node.Status);
    }

    [Fact]
    public void FromSensor_UsesAuthoritativeSensorProfileIdentityAndStatus()
    {
        var sensor = SensorProfile.Create(
            "Mesh thermometer",
            DeviceIdentifier.FromMacAddress("AA:BB:CC:DD:EE:11"),
            new DeploymentLocation("Facility A", "Zone 1", "Node-01"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero));

        MeshNode node = MeshNode.FromSensor(sensor);

        Assert.Equal(sensor.Id, node.Key.SensorProfileId);
        Assert.Equal(SensorStatus.Offline, node.Status);
        Assert.False(node.IsForwardingEligible);
        Assert.Throws<ArgumentNullException>(() => MeshNode.FromSensor(null!));
    }

    [Fact]
    public void SensorStatusProjection_DoesNotMutateOriginalNode()
    {
        var offline = Node(SensorOneId, SensorStatus.Offline);
        MeshNode online = offline.WithStatus(SensorStatus.Online);

        Assert.Equal(offline.Key, online.Key);
        Assert.Equal(SensorStatus.Offline, offline.Status);
        Assert.Equal(SensorStatus.Online, online.Status);
        Assert.False(offline.IsForwardingEligible);
        Assert.True(online.IsForwardingEligible);
    }

    [Fact]
    public void UndirectedLink_NormalisesReversedEndpointsAndPreservesLinkId()
    {
        MeshNodeKey gateway = MeshNodeKey.Gateway;
        MeshNodeKey sensor = MeshNodeKey.ForSensor(SensorOneId);

        var forward = new MeshLink(LinkOneId, gateway, sensor, 2.5);
        var reverse = new MeshLink(LinkOneId, sensor, gateway, 2.5);

        Assert.Equal(forward.NodeA, reverse.NodeA);
        Assert.Equal(forward.NodeB, reverse.NodeB);
        Assert.Equal(gateway, forward.NodeA);
        Assert.Equal(sensor, forward.NodeB);
        Assert.True(forward.Connects(sensor, gateway));
        Assert.True(forward.Contains(sensor));
        Assert.Equal(sensor, forward.OtherEndpoint(gateway));
        Assert.Equal(gateway, forward.OtherEndpoint(sensor));
        Assert.Equal(LinkOneId, forward.Id);
        Assert.Equal(2.5, forward.SimulatedEnergyCost);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void LinkCost_RejectsZeroNegativeAndNonFiniteValues(double cost)
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshLink(LinkOneId, MeshNodeKey.Gateway,
                MeshNodeKey.ForSensor(SensorOneId), cost));
    }

    [Fact]
    public void Link_RejectsInvalidIdentitySelfLinksAndUnknownEndpoints()
    {
        MeshNodeKey sensor = MeshNodeKey.ForSensor(SensorOneId);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshLink(Guid.Empty, MeshNodeKey.Gateway, sensor, 1));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshLink(LinkOneId, sensor, sensor, 1));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshLink(LinkOneId, MeshNodeKey.Gateway,
                MeshNodeKey.Gateway, 1));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshLink(LinkOneId, default, sensor, 1));

        var link = new MeshLink(LinkOneId, MeshNodeKey.Gateway, sensor, 1);
        Assert.Throws<InvalidMeshTopologyException>(() =>
            link.OtherEndpoint(MeshNodeKey.ForSensor(SensorTwoId)));
    }

    [Fact]
    public void LinkCostUpdate_CreatesNewImmutableLinkWithSameIdentity()
    {
        MeshLink first = GatewayLink(SensorOneId, LinkOneId, 2.5);
        MeshLink updated = first.WithSimulatedEnergyCost(0.75);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(first.NodeA, updated.NodeA);
        Assert.Equal(first.NodeB, updated.NodeB);
        Assert.Equal(2.5, first.SimulatedEnergyCost);
        Assert.Equal(0.75, updated.SimulatedEnergyCost);
        Assert.Throws<InvalidMeshTopologyException>(() =>
            first.WithSimulatedEnergyCost(double.NaN));
    }

    [Fact]
    public void GatewayOnlySnapshot_HasNoFabricatedSensorsOrLinks()
    {
        MeshGraphSnapshot snapshot = MeshGraphSnapshot.GatewayOnly();

        Assert.Equal(0L, snapshot.Version);
        Assert.Equal(MeshNodeKey.Gateway, Assert.Single(snapshot.Nodes).Key);
        Assert.Empty(snapshot.Links);
    }

    [Fact]
    public void Snapshot_OrdersIndependentInputsDeterministically()
    {
        MeshNode one = Node(SensorOneId, SensorStatus.Online);
        MeshNode two = Node(SensorTwoId, SensorStatus.Warning);
        var links = new[]
        {
            new MeshLink(LinkTwoId, one.Key, two.Key, 3),
            GatewayLink(SensorOneId, LinkOneId, 1)
        };

        var snapshot = new MeshGraphSnapshot(
            5,
            new[] { two, one, MeshNode.Gateway },
            links);

        Assert.Equal(5L, snapshot.Version);
        Assert.Equal(
            new[] { MeshNodeKey.Gateway, one.Key, two.Key },
            snapshot.Nodes.Select(node => node.Key).ToArray());
        Assert.Equal(
            new[] { LinkOneId, LinkTwoId },
            snapshot.Links.Select(link => link.Id).ToArray());
    }

    [Fact]
    public void Snapshot_ReadOnlyCollectionsDoNotChangeWhenSourceListsChange()
    {
        var nodes = new List<MeshNode>
        {
            MeshNode.Gateway,
            Node(SensorOneId, SensorStatus.Online)
        };
        var links = new List<MeshLink>
        {
            GatewayLink(SensorOneId, LinkOneId, 1)
        };
        var snapshot = new MeshGraphSnapshot(1, nodes, links);
        nodes.Clear();
        links.Clear();

        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Links);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNode>)snapshot.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshLink>)snapshot.Links).Clear());
    }

    [Fact]
    public void Snapshot_RejectsMissingDuplicateOrInvalidGateway()
    {
        MeshNode one = Node(SensorOneId, SensorStatus.Online);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0, new[] { one }, Array.Empty<MeshLink>()));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0,
                new[] { MeshNode.Gateway, MeshNode.Gateway },
                Array.Empty<MeshLink>()));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0,
                new[] { MeshNode.Gateway, one, one },
                Array.Empty<MeshLink>()));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(-1,
                new[] { MeshNode.Gateway }, Array.Empty<MeshLink>()));
    }

    [Fact]
    public void Snapshot_RejectsUnregisteredEndpointsAndDuplicateUndirectedPairs()
    {
        MeshNode one = Node(SensorOneId, SensorStatus.Online);
        MeshNode two = Node(SensorTwoId, SensorStatus.Online);
        MeshLink gatewayToOne = GatewayLink(SensorOneId, LinkOneId, 2);
        MeshLink samePairReversed = new(
            LinkTwoId, one.Key, MeshNodeKey.Gateway, 3);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0,
                new[] { MeshNode.Gateway, one },
                new[] { new MeshLink(LinkOneId, one.Key, two.Key, 2) }));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0,
                new[] { MeshNode.Gateway, one },
                new[] { gatewayToOne, samePairReversed }));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(0,
                new[] { MeshNode.Gateway, one, two },
                new[] { gatewayToOne,
                    new MeshLink(LinkOneId, one.Key, two.Key, 3) }));
    }

    [Fact]
    public void Snapshot_PreservesOfflineInventoryButRejectsDecommissionedLinks()
    {
        MeshNode offline = Node(SensorOneId, SensorStatus.Offline);
        MeshLink link = GatewayLink(SensorOneId, LinkOneId, 2);

        var snapshot = new MeshGraphSnapshot(
            0, new[] { MeshNode.Gateway, offline }, new[] { link });
        Assert.False(snapshot.Nodes[1].IsForwardingEligible);
        Assert.Single(snapshot.Links);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGraphSnapshot(
                1,
                new[] { MeshNode.Gateway,
                    offline.WithStatus(SensorStatus.Decommissioned) },
                new[] { link }));
    }

    private static MeshNode Node(Guid id, SensorStatus status) =>
        new(MeshNodeKey.ForSensor(id), status);

    private static MeshLink GatewayLink(Guid id, Guid linkId, double cost) =>
        new(linkId, MeshNodeKey.Gateway, MeshNodeKey.ForSensor(id), cost);
}