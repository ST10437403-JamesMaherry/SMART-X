using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Tests.Topology;

/// <summary>
/// P3-05 Commit 1: hand-verified BFS reachability and fewest-hop counts.
/// Route reconstruction and component classification are Commit 2; more
/// adversarial traversal fixtures belong to Commit 3.
/// </summary>
public sealed class MeshTraversalServiceTests
{
    [Fact]
    public void GatewayOnlyGraph_HasOneReachableVertexAtZeroHops()
    {
        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(
                MeshGraphSnapshot.GatewayOnly(41));

        Assert.Equal(41, result.GraphVersion);
        Assert.Single(result.ReachableNodes);
        Assert.Equal(0, result.ReachableSensorCount);
        AssertHop(result, MeshNodeKey.Gateway, 0);
    }

    [Fact]
    public void RegisteredButUnlinkedSensors_DoNotBecomeReachable()
    {
        MeshNodeKey one = Sensor(1);
        MeshNodeKey two = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(5,
            [Node(one), Node(two)], []);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        Assert.Single(result.ReachableNodes);
        Assert.False(result.TryGetHopCount(one, out _));
        Assert.False(result.TryGetHopCount(two, out _));
    }

    [Fact]
    public void DirectGatewayLink_HasExactlyOneHop()
    {
        MeshNodeKey one = Sensor(1);
        MeshGraphSnapshot graph = Snapshot(8, [Node(one)],
            [Link(1, MeshNodeKey.Gateway, one)]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        AssertHop(result, MeshNodeKey.Gateway, 0);
        AssertHop(result, one, 1);
        Assert.Equal(1, result.ReachableSensorCount);
    }

    [Fact]
    public void MultiHopChain_ReportsExactFewestHopDistance()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Snapshot(12,
            [Node(c), Node(a), Node(b)],
            [
                Link(3, b, c),
                Link(1, MeshNodeKey.Gateway, a),
                Link(2, a, b)
            ]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        AssertHop(result, a, 1);
        AssertHop(result, b, 2);
        AssertHop(result, c, 3);
        Assert.Equal(3, result.ReachableSensorCount);
        Assert.Equal(new[] { MeshNodeKey.Gateway, a, b, c },
            result.ReachableNodes.Select(entry => entry.Node).ToArray());
    }

    [Fact]
    public void ExpensiveDirectLink_BeatsCheaperTwoHopRouteForBfs()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(22,
            [Node(a), Node(b)],
            [
                Link(1, MeshNodeKey.Gateway, a, 1000),
                Link(2, MeshNodeKey.Gateway, b, 1),
                Link(3, b, a, 1)
            ]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        // BFS minimises the NUMBER of links, not their energy-weight sum.
        AssertHop(result, a, 1);
        AssertHop(result, b, 1);
    }

    [Fact]
    public void CyclicGraph_TerminatesAndVisitsEachVertexOnlyOnce()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshGraphSnapshot graph = Snapshot(23,
            [Node(a), Node(b), Node(c)],
            [
                Link(1, MeshNodeKey.Gateway, a),
                Link(2, a, b),
                Link(3, b, c),
                Link(4, c, a)
            ]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        AssertHop(result, a, 1);
        AssertHop(result, b, 2);
        AssertHop(result, c, 2);
        Assert.Equal(4, result.ReachableNodes.Count);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    public void NonForwardingBridge_BlocksDownstreamSensor(
        SensorStatus unavailableStatus)
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(14,
            [Node(bridge, unavailableStatus), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge),
                Link(2, bridge, leaf)
            ]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        Assert.Single(result.ReachableNodes);
        Assert.False(result.TryGetHopCount(bridge, out _));
        Assert.False(result.TryGetHopCount(leaf, out _));
    }

    [Fact]
    public void WarningBridge_RemainsEligibleToForward()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        MeshGraphSnapshot graph = Snapshot(17,
            [Node(bridge, SensorStatus.Warning), Node(leaf)],
            [
                Link(1, MeshNodeKey.Gateway, bridge),
                Link(2, bridge, leaf)
            ]);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        AssertHop(result, bridge, 1);
        AssertHop(result, leaf, 2);
    }

    [Fact]
    public void DecommissionedSensor_IsRetainedInInventoryButNotTraversed()
    {
        MeshNodeKey inactive = Sensor(1);
        MeshGraphSnapshot graph = Snapshot(18,
            [Node(inactive, SensorStatus.Decommissioned)], []);

        MeshGatewayReachability result =
            MeshTraversalService.CalculateGatewayReachability(graph);

        Assert.Single(result.ReachableNodes);
        Assert.False(result.TryGetHopCount(inactive, out _));
    }

    [Fact]
    public void ShuffledInputOrdering_ProducesStableOutputForTheSameGraph()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshLink first = Link(1, MeshNodeKey.Gateway, b);
        MeshLink second = Link(2, b, c);
        MeshLink third = Link(3, b, a);

        MeshGraphSnapshot forward = Snapshot(30,
            [Node(a), Node(b), Node(c)],
            [first, second, third]);
        MeshGraphSnapshot reverse = Snapshot(30,
            [Node(c), Node(b), Node(a)],
            [third, first, second]);

        MeshGatewayReachability left =
            MeshTraversalService.CalculateGatewayReachability(forward);
        MeshGatewayReachability right =
            MeshTraversalService.CalculateGatewayReachability(reverse);

        Assert.Equal(
            left.ReachableNodes.ToArray(),
            right.ReachableNodes.ToArray());
        Assert.Equal(new[] { MeshNodeKey.Gateway, a, b, c },
            left.ReachableNodes.Select(entry => entry.Node).ToArray());
    }

    [Fact]
    public async Task Service_UsesOneAuthoritativeSnapshotAndReturnsItsVersion()
    {
        MeshNodeKey sensor = Sensor(1);
        var store = new SnapshotOnlyTopologyStore(Snapshot(81,
            [Node(sensor)], [Link(1, MeshNodeKey.Gateway, sensor)]));
        var service = new MeshTraversalService(store);
        using var source = new CancellationTokenSource();

        MeshGatewayReachability result =
            await service.GetGatewayReachabilityAsync(source.Token);

        Assert.Equal(1, store.ReadCount);
        Assert.Equal(source.Token, store.LastToken);
        Assert.Equal(81, result.GraphVersion);
        AssertHop(result, sensor, 1);
    }

    [Fact]
    public async Task Service_DoesNotCacheStaleLinksOrSensorHealth()
    {
        MeshNodeKey bridge = Sensor(1);
        MeshNodeKey leaf = Sensor(2);
        var links = new[]
        {
            Link(1, MeshNodeKey.Gateway, bridge),
            Link(2, bridge, leaf)
        };
        var store = new SnapshotOnlyTopologyStore(Snapshot(10,
            [Node(bridge), Node(leaf)], links));
        var service = new MeshTraversalService(store);

        MeshGatewayReachability before = await service.GetGatewayReachabilityAsync();
        store.Snapshot = Snapshot(11,
            [Node(bridge, SensorStatus.Offline), Node(leaf)], links);
        MeshGatewayReachability after = await service.GetGatewayReachabilityAsync();

        Assert.Equal(2, store.ReadCount);
        AssertHop(before, leaf, 2);
        Assert.False(after.TryGetHopCount(leaf, out _));
        Assert.Equal(10, before.GraphVersion);
        Assert.Equal(11, after.GraphVersion);
    }

    [Fact]
    public async Task CancelledToken_StopsServiceBeforeAccessingTheStore()
    {
        var store = new SnapshotOnlyTopologyStore(MeshGraphSnapshot.GatewayOnly());
        var service = new MeshTraversalService(store);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetGatewayReachabilityAsync(source.Token));
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public void StaticTraversal_RejectsNullSnapshotAndCancellation()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MeshTraversalService.CalculateGatewayReachability(null!));

        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MeshTraversalService.CalculateGatewayReachability(
                MeshGraphSnapshot.GatewayOnly(), source.Token));
    }

    [Fact]
    public void Service_RejectsMissingTopologyStore()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MeshTraversalService(null!));
    }

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:D12}"));

    private static MeshNode Node(
        MeshNodeKey key,
        SensorStatus status = SensorStatus.Online) => new(key, status);

    private static MeshLink Link(
        int ordinal,
        MeshNodeKey first,
        MeshNodeKey second,
        double energyCost = 1) =>
        new(Guid.Parse($"10000000-0000-0000-0000-{ordinal:D12}"),
            first, second, energyCost);

    private static MeshGraphSnapshot Snapshot(
        long version,
        IReadOnlyList<MeshNode> sensors,
        IReadOnlyList<MeshLink> links) =>
        new(version, [MeshNode.Gateway, .. sensors], links);

    private static void AssertHop(
        MeshGatewayReachability result,
        MeshNodeKey key,
        int expected)
    {
        Assert.True(result.TryGetHopCount(key, out int actual));
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Intentionally supports only immutable snapshot reads. No BFS test
    /// should add links or change registered sensor state behind the scenes.
    /// </summary>
    private sealed class SnapshotOnlyTopologyStore : IMeshTopologyStore
    {
        public SnapshotOnlyTopologyStore(MeshGraphSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public MeshGraphSnapshot Snapshot { get; set; }

        public int ReadCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<MeshGraphSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            LastToken = cancellationToken;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<MeshLink>> GetIncidentLinksAsync(
            MeshNodeKey node,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MeshLink> AddLinkAsync(
            MeshLink link,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveLinkAsync(
            Guid linkId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MeshLink> UpdateLinkCostAsync(
            Guid linkId,
            double simulatedEnergyCost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}