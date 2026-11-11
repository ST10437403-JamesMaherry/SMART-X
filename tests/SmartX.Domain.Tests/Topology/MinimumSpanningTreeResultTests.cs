using SmartX.Domain.Topology;

namespace SmartX.Domain.Tests.Topology;

/// <summary>
/// P3-06 Commit 1: protect the immutable gateway-rooted MST contract from
/// duplicate parents, disconnected roots, cycles and misleading cost totals.
/// These are pure domain tests; no sensor registry or API is involved.
/// </summary>
public sealed class MinimumSpanningTreeResultTests
{
    [Fact]
    public void GatewayOnlyTree_HasZeroCostNoBranchesAndOneReachedVertex()
    {
        var result = new MinimumSpanningTreeResult(17, []);

        Assert.Equal(17, result.GraphVersion);
        Assert.Equal(0d, result.TotalSimulatedEnergyCost);
        Assert.Equal(0, result.SelectedEdgeCount);
        Assert.Equal(0, result.ReachableSensorCount);
        Assert.Equal(1, result.ReachedNodeCount);
        Assert.Empty(result.SelectedBranches);
        Assert.Empty(result.SelectedLinks);
        Assert.Equal(new[] { MeshNodeKey.Gateway }, result.ReachedNodes);
        Assert.True(result.ContainsNode(MeshNodeKey.Gateway));
        Assert.False(result.TryGetParent(MeshNodeKey.Gateway, out _));
        Assert.False(result.TryGetBranch(MeshNodeKey.Gateway, out _));
    }

    [Fact]
    public void SelectedBranches_AreOrderedByChildAndRetainExactParentLinks()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);
        MeshTreeBranch first = Branch(MeshNodeKey.Gateway, a, Link(1, MeshNodeKey.Gateway, a, 1));
        MeshTreeBranch second = Branch(a, b, Link(2, a, b, 2));
        MeshTreeBranch third = Branch(b, c, Link(3, b, c, 3));

        // The constructor must not rely on heap-selection or input order.
        var result = new MinimumSpanningTreeResult(93, [third, first, second]);

        Assert.Equal(93, result.GraphVersion);
        Assert.Equal(6d, result.TotalSimulatedEnergyCost);
        Assert.Equal(3, result.SelectedEdgeCount);
        Assert.Equal(3, result.ReachableSensorCount);
        Assert.Equal(4, result.ReachedNodeCount);
        Assert.Equal(new[] { MeshNodeKey.Gateway, a, b, c }, result.ReachedNodes);
        Assert.Equal(new[] { first, second, third }, result.SelectedBranches);
        Assert.Equal(new[] { first.Link, second.Link, third.Link }, result.SelectedLinks);
        Assert.True(result.ContainsNode(c));
        Assert.False(result.ContainsNode(Sensor(99)));
        Assert.True(result.TryGetParent(c, out MeshNodeKey parent));
        Assert.Equal(b, parent);
        Assert.True(result.TryGetBranch(b, out MeshTreeBranch? selected));
        Assert.Same(second, selected);
        Assert.False(result.TryGetParent(Sensor(99), out _));
        Assert.False(result.TryGetBranch(Sensor(99), out _));
    }

    [Fact]
    public void TreeResult_DefensivelyCopiesAndProtectsItsCollections()
    {
        MeshNodeKey a = Sensor(1);
        MeshTreeBranch branch = Branch(MeshNodeKey.Gateway, a,
            Link(11, MeshNodeKey.Gateway, a, 8));
        var input = new List<MeshTreeBranch> { branch };
        var result = new MinimumSpanningTreeResult(4, input);

        input.Clear();

        Assert.Single(result.SelectedBranches);
        Assert.Single(result.SelectedLinks);
        Assert.Equal(new[] { MeshNodeKey.Gateway, a }, result.ReachedNodes);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshTreeBranch>)result.SelectedBranches)[0] = branch);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshLink>)result.SelectedLinks)[0] = branch.Link);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshNodeKey>)result.ReachedNodes)[0] = a);
    }

    [Fact]
    public void TreeBranch_RequiresValidEndpointsAndAnActualMatchingLink()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshLink gatewayLink = Link(1, MeshNodeKey.Gateway, a, 2);

        Assert.Throws<ArgumentNullException>(() =>
            new MeshTreeBranch(MeshNodeKey.Gateway, a, null!));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeBranch(default, a, gatewayLink));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeBranch(MeshNodeKey.Gateway, default, gatewayLink));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeBranch(a, MeshNodeKey.Gateway, gatewayLink));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeBranch(a, a, gatewayLink));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MeshTreeBranch(MeshNodeKey.Gateway, b, gatewayLink));
    }

    [Fact]
    public void TreeBranch_CanOrientAnUndirectedLinkInEitherValidDirection()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshLink link = Link(5, a, b, 2);

        MeshTreeBranch forward = Branch(a, b, link);
        MeshTreeBranch reverse = Branch(b, a, link);

        Assert.Equal(a, forward.Parent);
        Assert.Equal(b, forward.Child);
        Assert.Equal(b, reverse.Parent);
        Assert.Equal(a, reverse.Child);
        Assert.Same(link, forward.Link);
        Assert.Same(link, reverse.Link);
    }

    [Fact]
    public void TreeResult_RejectsNegativeVersionNullInputAndNullBranches()
    {
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(-1, []));
        Assert.Throws<ArgumentNullException>(() =>
            new MinimumSpanningTreeResult(0, null!));
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(0, new MeshTreeBranch[] { null! }));
    }

    [Fact]
    public void TreeResult_RejectsTwoParentsForOneSensor()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(4,
            [
                Branch(MeshNodeKey.Gateway, a, Link(1, MeshNodeKey.Gateway, a, 1)),
                Branch(MeshNodeKey.Gateway, b, Link(2, MeshNodeKey.Gateway, b, 2)),
                Branch(b, a, Link(3, b, a, 1))
            ]));
    }

    [Fact]
    public void TreeResult_RejectsAParentMissingFromGatewayTree()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(10,
            [
                Branch(a, b, Link(7, a, b, 1))
            ]));
    }

    [Fact]
    public void TreeResult_RejectsParentCyclesEvenWithDistinctLinkIds()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshNodeKey c = Sensor(3);

        // A <- B <- C <- A. Each child is unique and each link is distinct,
        // but none of the three sensors can reach the gateway.
        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(3,
            [
                Branch(b, a, Link(1, b, a, 1)),
                Branch(c, b, Link(2, c, b, 1)),
                Branch(a, c, Link(3, a, c, 1))
            ]));
    }

    [Fact]
    public void TreeResult_RejectsReusingAnUndirectedLink()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);
        MeshLink shared = Link(99, a, b, 1);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(1,
            [
                Branch(b, a, shared),
                Branch(a, b, shared)
            ]));
    }

    [Fact]
    public void TreeResult_RejectsFiniteWeightsWhoseSumOverflows()
    {
        MeshNodeKey a = Sensor(1);
        MeshNodeKey b = Sensor(2);

        Assert.Throws<InvalidMeshTopologyException>(() =>
            new MinimumSpanningTreeResult(1,
            [
                Branch(MeshNodeKey.Gateway, a,
                    Link(1, MeshNodeKey.Gateway, a, double.MaxValue)),
                Branch(a, b, Link(2, a, b, double.MaxValue))
            ]));
    }

    [Fact]
    public void TreeLookups_RejectDefaultInvalidNodeKeys()
    {
        var result = new MinimumSpanningTreeResult(0, []);

        Assert.Throws<InvalidMeshTopologyException>(() => result.ContainsNode(default));
        Assert.Throws<InvalidMeshTopologyException>(() => result.TryGetParent(default, out _));
        Assert.Throws<InvalidMeshTopologyException>(() => result.TryGetBranch(default, out _));
    }

    private static MeshTreeBranch Branch(
        MeshNodeKey parent, MeshNodeKey child, MeshLink link) =>
        new(parent, child, link);

    private static MeshNodeKey Sensor(int ordinal) =>
        MeshNodeKey.ForSensor(Guid.Parse(
            $"00000000-0000-0000-0000-{ordinal:000000000000}"));

    private static MeshLink Link(
        int ordinal, MeshNodeKey first, MeshNodeKey second, double cost) =>
        new(Guid.Parse($"10000000-0000-0000-0000-{ordinal:000000000000}"),
            first, second, cost);
}