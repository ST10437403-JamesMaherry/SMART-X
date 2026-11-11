namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable, validated minimum-total-cost backbone for the gateway's
/// forwarding-eligible connected component in one mesh graph revision.
///
/// This is NOT a minimum-cost path to each sensor. It contains only sensors
/// reached from the gateway; disconnected/unavailable classifications and
/// tree-path reconstruction are added in P3-06 Commit 2.
/// </summary>
public sealed class MinimumSpanningTreeResult
{
    private readonly Dictionary<MeshNodeKey, MeshTreeBranch> _branchByChild;

    public MinimumSpanningTreeResult(
        long graphVersion,
        IEnumerable<MeshTreeBranch> selectedBranches)
    {
        if (graphVersion < 0)
        {
            throw new InvalidMeshTopologyException(
                "A spanning tree cannot reference a negative graph revision.");
        }

        ArgumentNullException.ThrowIfNull(selectedBranches);
        MeshTreeBranch[] input = selectedBranches.ToArray();
        var branchByChild = new Dictionary<MeshNodeKey, MeshTreeBranch>();
        var usedLinkIds = new HashSet<Guid>();
        var usedPairs = new HashSet<(MeshNodeKey NodeA, MeshNodeKey NodeB)>();
        double totalCost = 0d;

        foreach (MeshTreeBranch? branch in input)
        {
            if (branch is null)
            {
                throw new InvalidMeshTopologyException(
                    "A spanning tree cannot contain a null branch.");
            }

            if (!branchByChild.TryAdd(branch.Child, branch))
            {
                throw new InvalidMeshTopologyException(
                    "Each connected sensor must have exactly one tree parent.");
            }

            if (!usedLinkIds.Add(branch.Link.Id) ||
                !usedPairs.Add((branch.Link.NodeA, branch.Link.NodeB)))
            {
                throw new InvalidMeshTopologyException(
                    "A spanning tree cannot reuse an undirected link.");
            }

            // The link model guarantees strictly positive finite costs.
            // Guard the aggregate as well: adding finite doubles can overflow.
            totalCost += branch.Link.SimulatedEnergyCost;
            if (!double.IsFinite(totalCost))
            {
                throw new InvalidMeshTopologyException(
                    "The spanning tree's total simulated cost must be finite.");
            }
        }

        // Every non-gateway parent must itself appear as a selected child.
        // Otherwise the result would expose an invented, disconnected root.
        foreach (MeshTreeBranch branch in input)
        {
            if (!branch.Parent.IsGateway &&
                !branchByChild.ContainsKey(branch.Parent))
            {
                throw new InvalidMeshTopologyException(
                    "Every selected tree branch must ultimately reach the gateway.");
            }
        }

        // Check that all parent chains terminate at the gateway, not at a
        // cycle. Memoizing validated chains keeps this linear in tree size.
        var reachesGateway = new HashSet<MeshNodeKey> { MeshNodeKey.Gateway };
        foreach (MeshTreeBranch branch in input)
        {
            if (reachesGateway.Contains(branch.Child))
            {
                continue;
            }

            var currentChain = new List<MeshNodeKey>();
            var onChain = new HashSet<MeshNodeKey>();
            MeshNodeKey cursor = branch.Child;

            while (!reachesGateway.Contains(cursor))
            {
                if (!onChain.Add(cursor))
                {
                    throw new InvalidMeshTopologyException(
                        "A spanning tree cannot contain a parent cycle.");
                }

                if (!branchByChild.TryGetValue(cursor,
                        out MeshTreeBranch? parentBranch) || parentBranch is null)
                {
                    throw new InvalidMeshTopologyException(
                        "A spanning-tree parent chain is disconnected.");
                }

                currentChain.Add(cursor);
                cursor = parentBranch.Parent;
            }

            foreach (MeshNodeKey checkedNode in currentChain)
            {
                reachesGateway.Add(checkedNode);
            }
        }

        // Defensive copies are published in canonical order, independent of
        // heap insertion order or the caller's mutable input collection.
        MeshTreeBranch[] sortedBranches = input
            .OrderBy(branch => branch.Child)
            .ToArray();
        MeshLink[] sortedLinks = input
            .Select(branch => branch.Link)
            .OrderBy(link => link.NodeA)
            .ThenBy(link => link.NodeB)
            .ThenBy(link => link.Id)
            .ToArray();
        MeshNodeKey[] reached = new MeshNodeKey[checked(input.Length + 1)];
        reached[0] = MeshNodeKey.Gateway;
        for (int index = 0; index < sortedBranches.Length; index++)
        {
            reached[index + 1] = sortedBranches[index].Child;
        }

        _branchByChild = branchByChild;
        GraphVersion = graphVersion;
        TotalSimulatedEnergyCost = totalCost;
        SelectedBranches = Array.AsReadOnly(sortedBranches);
        SelectedLinks = Array.AsReadOnly(sortedLinks);
        ReachedNodes = Array.AsReadOnly(reached);
    }

    /// <summary>
    /// Revision of the single immutable graph used by Prim.
    /// </summary>
    public long GraphVersion { get; }

    /// <summary>
    /// Sum of selected link weights in relative simulation units, not
    /// calibrated battery consumption or the cost of an individual route.
    /// </summary>
    public double TotalSimulatedEnergyCost { get; }

    /// <summary>
    /// Exactly one parent branch for each gateway-connected sensor, sorted
    /// by child identity. The gateway itself has no parent branch.
    /// </summary>
    public IReadOnlyList<MeshTreeBranch> SelectedBranches { get; }

    /// <summary>
    /// Each selected undirected link exactly once in canonical edge order.
    /// </summary>
    public IReadOnlyList<MeshLink> SelectedLinks { get; }

    /// <summary>
    /// Gateway first, then connected sensors in ascending identity order.
    /// An isolated gateway yields a one-element collection.
    /// </summary>
    public IReadOnlyList<MeshNodeKey> ReachedNodes { get; }

    public int SelectedEdgeCount => SelectedBranches.Count;

    public int ReachableSensorCount => SelectedBranches.Count;

    public int ReachedNodeCount => ReachedNodes.Count;

    /// <summary>
    /// True only for the gateway and sensors in its selected tree. An
    /// absent key may be disconnected, unavailable or not registered;
    /// Commit 2 will provide those distinct classifications.
    /// </summary>
    public bool ContainsNode(MeshNodeKey node)
    {
        EnsureValid(node);
        return node.IsGateway || _branchByChild.ContainsKey(node);
    }

    public bool TryGetParent(MeshNodeKey node, out MeshNodeKey parent)
    {
        EnsureValid(node);
        if (_branchByChild.TryGetValue(node, out MeshTreeBranch? branch) &&
            branch is not null)
        {
            parent = branch.Parent;
            return true;
        }

        parent = default;
        return false;
    }

    /// <summary>
    /// A selected branch also exposes the exact original weighted link.
    /// This supports on-demand tree-route reconstruction in Commit 2.
    /// </summary>
    public bool TryGetBranch(MeshNodeKey node, out MeshTreeBranch? branch)
    {
        EnsureValid(node);
        return _branchByChild.TryGetValue(node, out branch);
    }

    private static void EnsureValid(MeshNodeKey node)
    {
        if (!node.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "A spanning-tree lookup requires a valid mesh vertex.");
        }
    }
}