namespace SmartX.Domain.Topology;

/// <summary>
/// One gateway-reachable vertex and its shortest unweighted distance from
/// the logical gateway. A hop counts a configured radio link, not energy.
/// </summary>
public readonly record struct MeshNodeHopCount
{
    public MeshNodeHopCount(MeshNodeKey node, int hopCount)
    {
        if (!node.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "A BFS hop-count entry requires a valid mesh vertex.");
        }

        if (hopCount < 0 || (node.IsGateway && hopCount != 0) ||
            (!node.IsGateway && hopCount == 0))
        {
            throw new InvalidMeshTopologyException(
                "The gateway must have zero hops and a reachable sensor must have at least one hop.");
        }

        Node = node;
        HopCount = hopCount;
    }

    public MeshNodeKey Node { get; }

    /// <summary>Minimum number of configured links from the gateway.</summary>
    public int HopCount { get; }
}