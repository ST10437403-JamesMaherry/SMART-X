using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// P3-05: breadth-first traversal of the real simulated mesh graph.
/// Reads exactly one immutable registry-aware snapshot per operation and
/// uses Queue&lt;T&gt; / HashSet&lt;T&gt; to calculate fewest-link hop counts.
///
/// A node forwards only if MeshNode.IsForwardingEligible is true. Offline,
/// Fault, Maintenance and Decommissioned sensors never bridge another sensor.
/// BFS is deliberately unweighted: Prim's energy-weighted MST is P3-06.
/// </summary>
public sealed class MeshTraversalService
{
    private readonly IMeshTopologyStore _topologyStore;

    public MeshTraversalService(IMeshTopologyStore topologyStore)
    {
        _topologyStore = topologyStore ??
            throw new ArgumentNullException(nameof(topologyStore));
    }

    /// <summary>
    /// Resolves the current authoritative sensor states and configured links
    /// once, then calculates a versioned gateway reachability result.
    /// </summary>
    public async Task<MeshGatewayReachability> GetGatewayReachabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return CalculateGatewayReachability(snapshot, cancellationToken);
    }

    /// <summary>
    /// Pure BFS over an immutable snapshot, allowing precise algorithm tests
    /// without modifying the sensor registry, topology store or live API.
    /// Traversal and result construction take O(V + E) time and space.
    /// </summary>
    public static MeshGatewayReachability CalculateGatewayReachability(
        MeshGraphSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        TraverseGateway(snapshot, cancellationToken).Reachability;

    /// <summary>
    /// Retains predecessors for Commit 2's sensor-to-gateway path builder.
    /// Its reachability projection is already useful as an isolated C1 change.
    /// </summary>
    private static GatewayTraversalState TraverseGateway(
        MeshGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        // Only forwarding-eligible vertices belong to the traversable graph.
        // The snapshot still retains offline/failed vertices for later
        // disconnected/unavailable-node reporting (Commit 2).
        var neighbours = new Dictionary<MeshNodeKey, List<MeshNodeKey>>();
        foreach (MeshNode node in snapshot.Nodes)
        {
            if (node.IsForwardingEligible)
            {
                neighbours.Add(node.Key, new List<MeshNodeKey>());
            }
        }

        // MeshGraphSnapshot.Links is already sorted by canonical (NodeA,
        // NodeB) keys. Appending the opposite endpoint in that order also
        // yields ascending neighbour keys for EVERY vertex: smaller
        // neighbours (where the vertex is NodeB) precede larger neighbours
        // (where it is NodeA). No per-node sorting or O(E log E) pass needed.
        foreach (MeshLink link in snapshot.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!neighbours.TryGetValue(link.NodeA, out List<MeshNodeKey>? first) ||
                !neighbours.TryGetValue(link.NodeB, out List<MeshNodeKey>? second))
            {
                continue; // An unavailable endpoint cannot forward.
            }

            first.Add(link.NodeB);
            second.Add(link.NodeA);
        }

        // P3-04 guarantees one logical, always-eligible gateway vertex.
        var pending = new Queue<MeshNodeKey>();
        var visited = new HashSet<MeshNodeKey> { MeshNodeKey.Gateway };
        var hopCounts = new Dictionary<MeshNodeKey, int>
        {
            [MeshNodeKey.Gateway] = 0
        };
        var predecessors = new Dictionary<MeshNodeKey, MeshNodeKey>();

        pending.Enqueue(MeshNodeKey.Gateway);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeshNodeKey current = pending.Dequeue();
            int nextHop = checked(hopCounts[current] + 1);

            foreach (MeshNodeKey next in neighbours[current])
            {
                // HashSet.Add returns false on cycles / duplicate discovery.
                // The first visit is necessarily the fewest-hop route.
                if (!visited.Add(next))
                {
                    continue;
                }

                hopCounts.Add(next, nextHop);
                predecessors.Add(next, current);
                pending.Enqueue(next);
            }
        }

        // Snapshot.Nodes is ordered by identity. Projecting through it
        // produces deterministic output without another sorting pass.
        var reachable = new List<MeshNodeHopCount>(hopCounts.Count);
        foreach (MeshNode node in snapshot.Nodes)
        {
            if (hopCounts.TryGetValue(node.Key, out int hopCount))
            {
                reachable.Add(new MeshNodeHopCount(node.Key, hopCount));
            }
        }

        return new GatewayTraversalState(
            new MeshGatewayReachability(snapshot.Version, reachable),
            predecessors);
    }

    /// <summary>
    /// Internal computation state for the next commit; never exposes a
    /// mutable predecessor dictionary to an API or frontend consumer.
    /// </summary>
    private sealed record GatewayTraversalState(
        MeshGatewayReachability Reachability,
        Dictionary<MeshNodeKey, MeshNodeKey> Predecessors);
}