namespace SmartX.Domain.Topology;

/// <summary>
/// Defensive, immutable gateway BFS projection for exactly one graph revision.
/// Includes the gateway (zero hops) and forwarding-eligible vertices actually
/// reached through configured links. Unreachable-node classification and
/// explicit sensor-to-gateway paths are added in P3-05 Commit 2.
/// </summary>
public sealed class MeshGatewayReachability
{
    private readonly Dictionary<MeshNodeKey, int> _hopCounts;

    public MeshGatewayReachability(
        long graphVersion,
        IEnumerable<MeshNodeHopCount> reachableNodes)
    {
        if (graphVersion < 0)
        {
            throw new InvalidMeshTopologyException(
                "A reachability result cannot have a negative graph version.");
        }

        ArgumentNullException.ThrowIfNull(reachableNodes);

        MeshNodeHopCount[] entries = reachableNodes.ToArray();
        if (entries.Length == 0 || entries[0].Node != MeshNodeKey.Gateway ||
            entries[0].HopCount != 0)
        {
            throw new InvalidMeshTopologyException(
                "A gateway BFS result must begin with the gateway at zero hops.");
        }

        // The service consumes an already ordered MeshGraphSnapshot. Checking
        // that order here is linear and avoids an unnecessary sort in BFS.
        _hopCounts = new Dictionary<MeshNodeKey, int>(entries.Length);
        MeshNodeKey? previous = null;

        foreach (MeshNodeHopCount entry in entries)
        {
            if (!entry.Node.IsValid || entry.HopCount < 0 ||
                (entry.Node.IsGateway && entry.HopCount != 0) ||
                (!entry.Node.IsGateway && entry.HopCount == 0))
            {
                throw new InvalidMeshTopologyException(
                    "A gateway BFS result contains an invalid node or hop count.");
            }

            if (previous is not null && previous.Value.CompareTo(entry.Node) >= 0)
            {
                throw new InvalidMeshTopologyException(
                    "Reachable mesh vertices must be unique and ordered by node identity.");
            }

            if (!_hopCounts.TryAdd(entry.Node, entry.HopCount))
            {
                throw new InvalidMeshTopologyException(
                    "A gateway BFS result cannot repeat a mesh vertex.");
            }

            previous = entry.Node;
        }

        GraphVersion = graphVersion;
        ReachableNodes = Array.AsReadOnly(entries);
    }

    /// <summary>
    /// Version of the immutable graph snapshot used for this calculation;
    /// a later request can use a newer revision after a sensor/link change.
    /// </summary>
    public long GraphVersion { get; }

    /// <summary>
    /// Reachable vertices in canonical key order, including the gateway.
    /// No caller can mutate the internal array through this list.
    /// </summary>
    public IReadOnlyList<MeshNodeHopCount> ReachableNodes { get; }

    public int ReachableSensorCount => ReachableNodes.Count - 1;

    /// <summary>
    /// Returns false for an existing but unreachable/offline sensor. This
    /// method does not conflate that state with a missing registry entry;
    /// explicit result classification follows in P3-05 Commit 2.
    /// </summary>
    public bool TryGetHopCount(MeshNodeKey node, out int hopCount)
    {
        if (!node.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "A hop-count lookup requires a valid mesh vertex key.");
        }

        return _hopCounts.TryGetValue(node, out hopCount);
    }
}