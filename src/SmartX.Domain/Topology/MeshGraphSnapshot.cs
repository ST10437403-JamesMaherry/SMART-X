using SmartX.Domain.Sensors;

namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable, deterministic view of one graph revision. The backing arrays
/// are owned by this snapshot and exposed only through read-only wrappers.
/// Nodes and links are immutable, so later store writes cannot change a
/// previously returned response or traversal input.
///
/// This is the graph model, NOT the existing deployment-validation tree.
/// </summary>
public sealed class MeshGraphSnapshot
{
    public MeshGraphSnapshot(
        long version,
        IEnumerable<MeshNode> nodes,
        IEnumerable<MeshLink> links)
    {
        if (version < 0)
        {
            throw new InvalidMeshTopologyException(
                "A mesh graph version cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(links);

        MeshNode[] nodeArray = nodes.ToArray();
        MeshLink[] linkArray = links.ToArray();

        if (nodeArray.Any(node => node is null || !node.Key.IsValid))
        {
            throw new InvalidMeshTopologyException(
                "A mesh snapshot contains a null or invalid vertex.");
        }

        if (nodeArray.Count(node => node.IsGateway) != 1)
        {
            throw new InvalidMeshTopologyException(
                "Every mesh snapshot requires exactly one logical gateway.");
        }

        var nodeByKey = new Dictionary<MeshNodeKey, MeshNode>();
        foreach (MeshNode node in nodeArray)
        {
            if (!nodeByKey.TryAdd(node.Key, node))
            {
                throw new InvalidMeshTopologyException(
                    "A mesh snapshot contains a duplicate vertex identity.");
            }
        }

        var linkIds = new HashSet<Guid>();
        var unorderedPairs =
            new HashSet<(MeshNodeKey NodeA, MeshNodeKey NodeB)>();

        foreach (MeshLink? link in linkArray)
        {
            if (link is null)
            {
                throw new InvalidMeshTopologyException(
                    "A mesh snapshot cannot contain a null link.");
            }

            if (!nodeByKey.TryGetValue(link.NodeA, out MeshNode? first) ||
                !nodeByKey.TryGetValue(link.NodeB, out MeshNode? second))
            {
                throw new InvalidMeshTopologyException(
                    "A mesh link references a vertex absent from this snapshot.");
            }

            if (first.Status == SensorStatus.Decommissioned ||
                second.Status == SensorStatus.Decommissioned)
            {
                throw new InvalidMeshTopologyException(
                    "A decommissioned sensor cannot retain a mesh connection.");
            }

            if (!linkIds.Add(link.Id))
            {
                throw new InvalidMeshTopologyException(
                    "A mesh snapshot contains a repeated link identity.");
            }

            if (!unorderedPairs.Add((link.NodeA, link.NodeB)))
            {
                throw new InvalidMeshTopologyException(
                    "A mesh snapshot contains duplicate undirected links.");
            }
        }

        Version = version;
        Nodes = Array.AsReadOnly(nodeArray.OrderBy(node => node.Key).ToArray());
        Links = Array.AsReadOnly(linkArray
            .OrderBy(link => link.NodeA)
            .ThenBy(link => link.NodeB)
            .ThenBy(link => link.Id)
            .ToArray());
    }

    /// <summary>
    /// Monotonic store revision, allowing consumers to detect an updated mesh.
    /// </summary>
    public long Version { get; }

    public IReadOnlyList<MeshNode> Nodes { get; }

    /// <summary>
    /// Each undirected connection appears exactly once in canonical order.
    /// The adjacency-list store will maintain both directions internally.
    /// </summary>
    public IReadOnlyList<MeshLink> Links { get; }

    public static MeshGraphSnapshot GatewayOnly(long version = 0) =>
        new(version, new[] { MeshNode.Gateway }, Array.Empty<MeshLink>());
}