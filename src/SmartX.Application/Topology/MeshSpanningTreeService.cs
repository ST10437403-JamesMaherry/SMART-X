using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// P3-06: gateway-rooted Prim minimum spanning tree over the real simulated
/// mesh graph. Uses the built-in .NET PriorityQueue as a min-heap, plus a
/// visited set to accept each sensor exactly once.
///
/// Prim minimises the SUM of chosen undirected link costs across the
/// gateway-connected component. It does not minimise every sensor's path
/// cost, and it does not create or discover any physical radio connections.
/// </summary>
public sealed class MeshSpanningTreeService
{
    private readonly IMeshTopologyStore _topologyStore;

    public MeshSpanningTreeService(IMeshTopologyStore topologyStore)
    {
        _topologyStore = topologyStore ??
            throw new ArgumentNullException(nameof(topologyStore));
    }

    /// <summary>
    /// Reads exactly one reconciled, immutable graph revision. A later call
    /// fetches a new revision rather than reusing a stale computed tree.
    /// </summary>
    public async Task<MinimumSpanningTreeResult> GetGatewayMinimumSpanningTreeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return CalculateGatewayMinimumSpanningTree(snapshot, cancellationToken);
    }

    /// <summary>
    /// Reads a single authoritative graph revision, then combines Prim's
    /// selected weighted tree with P3-05's connectivity classifications.
    /// No second store read can mix old parents with new health states.
    /// </summary>
    public async Task<MeshGatewaySpanningTreeRouting> GetGatewaySpanningTreeRoutingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return CalculateGatewaySpanningTreeRouting(snapshot, cancellationToken);
    }

    /// <summary>
    /// Returns the selected MST path and its accumulated simulated cost for
    /// a registered sensor, or null for an unknown sensor profile. Offline or
    /// disconnected sensors return an explicit status, not an invented path.
    /// </summary>
    public async Task<MeshTreeRoute?> GetSensorTreeRouteAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A tree-route lookup requires a non-empty sensor profile ID.");
        }

        MeshGatewaySpanningTreeRouting projection =
            await GetGatewaySpanningTreeRoutingAsync(cancellationToken)
                .ConfigureAwait(false);

        projection.TryGetTreeRouteToGateway(
            MeshNodeKey.ForSensor(sensorProfileId), out MeshTreeRoute? route);
        return route;
    }

    /// <summary>
    /// Pure projection over ONE immutable snapshot. Prim supplies selected
    /// weighted parent links; existing BFS supplies only the membership and
    /// disconnected-component partition. Actual tree routes NEVER use the
    /// BFS predecessor map. Cost is O(E log E + V log V) including Prim's
    /// result ordering and one O(V + E) BFS classification pass.
    /// </summary>
    public static MeshGatewaySpanningTreeRouting CalculateGatewaySpanningTreeRouting(
        MeshGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        MinimumSpanningTreeResult tree =
            CalculateGatewayMinimumSpanningTree(snapshot, cancellationToken);
        MeshGatewayRouting classification =
            MeshTraversalService.CalculateGatewayRouting(snapshot, cancellationToken);

        // Prim and BFS must identify the SAME forwarding-eligible gateway
        // component, despite selecting different edges and routes within it.
        // This also catches accidental changes to the eligibility policy.
        if (tree.GraphVersion != classification.GraphVersion ||
            !tree.ReachedNodes.SequenceEqual(
                classification.Reachability.ReachableNodes.Select(node => node.Node)))
        {
            throw new InvalidMeshTopologyException(
                "Prim and BFS disagree about gateway-connected mesh membership.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new MeshGatewaySpanningTreeRouting(
            tree,
            classification.RegisteredSensors,
            classification.UnavailableSensors,
            classification.DisconnectedComponents);
    }

    /// <summary>
    /// Pure Prim calculation on a single graph snapshot. The gateway is the
    /// fixed root. A candidate is accepted only when its child has not been
    /// visited, preventing cycles even in dense undirected graphs.
    ///
    /// Building adjacency costs O(V + E); each eligible edge is enqueued at
    /// most once, so heap work is O(E log E) in the edge-frontier form of
    /// Prim, plus canonical result sorting. Memory is O(V + E).
    /// </summary>
    public static MinimumSpanningTreeResult CalculateGatewayMinimumSpanningTree(
        MeshGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        // Retain only forwarding-eligible endpoints. An offline/faulted or
        // maintenance sensor remains inventoried in the source snapshot but
        // is never treated as a relay or a connected MST vertex.
        var adjacency = new Dictionary<MeshNodeKey, List<MeshLink>>();
        foreach (MeshNode node in snapshot.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.IsForwardingEligible)
            {
                adjacency.Add(node.Key, new List<MeshLink>());
            }
        }

        foreach (MeshLink link in snapshot.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (adjacency.TryGetValue(link.NodeA, out List<MeshLink>? first) &&
                adjacency.TryGetValue(link.NodeB, out List<MeshLink>? second))
            {
                first.Add(link);
                second.Add(link);
            }
        }

        var visited = new HashSet<MeshNodeKey> { MeshNodeKey.Gateway };
        var selected = new List<MeshTreeBranch>();
        var frontier = new PriorityQueue<FrontierEdge, FrontierPriority>();

        // Only expand vertices after they have joined the gateway's tree.
        // A link is queued only from its first visited endpoint toward its
        // unvisited endpoint. The reverse direction is never queued later.
        void EnqueueOutgoing(MeshNodeKey parent)
        {
            foreach (MeshLink link in adjacency[parent])
            {
                cancellationToken.ThrowIfCancellationRequested();
                MeshNodeKey child = link.OtherEndpoint(parent);
                if (visited.Contains(child))
                {
                    continue;
                }

                frontier.Enqueue(
                    new FrontierEdge(parent, child, link),
                    new FrontierPriority(
                        link.SimulatedEnergyCost,
                        link.NodeA,
                        link.NodeB,
                        link.Id));
            }
        }

        EnqueueOutgoing(MeshNodeKey.Gateway);

        while (frontier.TryDequeue(out FrontierEdge candidate, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Competing edges to the same child may remain in the heap;
            // whichever was cheapest to cross the cut is accepted first.
            if (!visited.Add(candidate.Child))
            {
                continue;
            }

            selected.Add(new MeshTreeBranch(
                candidate.Parent, candidate.Child, candidate.Link));
            EnqueueOutgoing(candidate.Child);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Count != visited.Count - 1)
        {
            throw new InvalidMeshTopologyException(
                "A gateway-rooted spanning tree must have reached nodes minus one edges.");
        }

        return new MinimumSpanningTreeResult(snapshot.Version, selected);
    }

    private readonly record struct FrontierEdge(
        MeshNodeKey Parent,
        MeshNodeKey Child,
        MeshLink Link);

    /// <summary>
    /// Stable priority independent of insertion order or runtime heap
    /// implementation. Compare by cost, then canonical endpoint identities,
    /// then immutable link ID. This resolves equal weights reproducibly.
    /// </summary>
    private readonly record struct FrontierPriority(
        double Cost,
        MeshNodeKey NodeA,
        MeshNodeKey NodeB,
        Guid LinkId) : IComparable<FrontierPriority>
    {
        public int CompareTo(FrontierPriority other)
        {
            int comparison = Cost.CompareTo(other.Cost);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = NodeA.CompareTo(other.NodeA);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = NodeB.CompareTo(other.NodeB);
            return comparison != 0
                ? comparison
                : LinkId.CompareTo(other.LinkId);
        }
    }
}