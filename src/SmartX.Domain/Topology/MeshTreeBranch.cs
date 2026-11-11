namespace SmartX.Domain.Topology;

/// <summary>
/// A selected, directed parent-child relationship in a gateway-rooted
/// minimum spanning tree. The underlying <see cref="MeshLink"/> remains
/// undirected; Parent and Child describe only the chosen tree orientation.
/// </summary>
public sealed class MeshTreeBranch
{
    public MeshTreeBranch(MeshNodeKey parent, MeshNodeKey child, MeshLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!parent.IsValid || !child.IsValid || child.IsGateway ||
            parent == child || !link.Connects(parent, child))
        {
            throw new InvalidMeshTopologyException(
                "A spanning-tree branch requires a valid sensor child, a " +
                "distinct parent, and a link joining those exact vertices.");
        }

        Parent = parent;
        Child = child;
        Link = link;
    }

    /// <summary>
    /// Already-connected vertex from which Prim accepted the crossing edge.
    /// </summary>
    public MeshNodeKey Parent { get; }

    /// <summary>
    /// Newly connected sensor, which must not be the gateway.
    /// </summary>
    public MeshNodeKey Child { get; }

    /// <summary>
    /// Original immutable configured link, including its simulated cost.
    /// </summary>
    public MeshLink Link { get; }
}