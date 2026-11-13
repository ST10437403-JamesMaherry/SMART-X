using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-05 Commit 1: verify that BFS result contracts cannot publish invalid,
/// unsorted, duplicate or externally mutable hop-count projections.
/// </summary>
public sealed class MeshGatewayReachabilityTests
{
    private static readonly MeshNodeKey First = MeshNodeKey.ForSensor(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));

    private static readonly MeshNodeKey Second = MeshNodeKey.ForSensor(
        Guid.Parse("00000000-0000-0000-0000-000000000002"));

    [Fact]
    public void GatewayOnlyResult_ReportsZeroHopsAndNoReachableSensors()
    {
        var result = new MeshGatewayReachability(
            17, [new MeshNodeHopCount(MeshNodeKey.Gateway, 0)]);

        Assert.Equal(17, result.GraphVersion);
        Assert.Single(result.ReachableNodes);
        Assert.Equal(0, result.ReachableSensorCount);
        Assert.True(result.TryGetHopCount(MeshNodeKey.Gateway, out int hops));
        Assert.Equal(0, hops);
        Assert.False(result.TryGetHopCount(First, out _));
    }

    [Fact]
    public void NodeHopCount_RejectsInvalidKeysAndImpossibleHopValues()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNodeHopCount(default, 2));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNodeHopCount(MeshNodeKey.Gateway, -1));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNodeHopCount(MeshNodeKey.Gateway, 1));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNodeHopCount(First, 0));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshNodeHopCount(First, -7));
    }

    [Fact]
    public void Result_RejectsNegativeVersionAndNullSequence()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(-1, [new MeshNodeHopCount(MeshNodeKey.Gateway, 0)]));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshGatewayReachability(0, null!));
    }

    [Fact]
    public void Result_RequiresGatewayAsFirstAndZeroHopEntry()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0, []));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0, [new MeshNodeHopCount(First, 1)]));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0,
            [
                new MeshNodeHopCount(First, 1),
                new MeshNodeHopCount(MeshNodeKey.Gateway, 0)
            ]));
    }

    [Fact]
    public void Result_RejectsDuplicateOrOutOfOrderSensorKeys()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0,
            [
                new MeshNodeHopCount(MeshNodeKey.Gateway, 0),
                new MeshNodeHopCount(First, 1),
                new MeshNodeHopCount(First, 2)
            ]));

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0,
            [
                new MeshNodeHopCount(MeshNodeKey.Gateway, 0),
                new MeshNodeHopCount(Second, 1),
                new MeshNodeHopCount(First, 2)
            ]));
    }

    [Fact]
    public void Result_RejectsDefaultConstructedRecordStructs()
    {
        // A default struct bypasses its constructor; the result validates
        // entries again rather than trusting their construction history.
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshGatewayReachability(0,
            [
                new MeshNodeHopCount(MeshNodeKey.Gateway, 0),
                default(MeshNodeHopCount)
            ]));
    }

    [Fact]
    public void Result_CopiesInputAndRejectsMutationThroughReadOnlyList()
    {
        var supplied = new List<MeshNodeHopCount>
        {
            new(MeshNodeKey.Gateway, 0),
            new(First, 1)
        };
        var result = new MeshGatewayReachability(9, supplied);

        supplied.Clear();

        Assert.Equal(2, result.ReachableNodes.Count);
        Assert.True(result.TryGetHopCount(First, out int hops));
        Assert.Equal(1, hops);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeHopCount>)result.ReachableNodes)[1] =
                new MeshNodeHopCount(Second, 1));
    }

    [Fact]
    public void Lookup_RejectsInvalidIdentityWithoutExposingItsInternalIndex()
    {
        var result = new MeshGatewayReachability(
            0, [new MeshNodeHopCount(MeshNodeKey.Gateway, 0)]);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            result.TryGetHopCount(default, out _));
    }
}