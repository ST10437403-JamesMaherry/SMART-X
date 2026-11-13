namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable, version-consistent connectivity summary for one gateway-rooted
/// Prim tree. Membership is partitioned into reached, disconnected and
/// unavailable registered sensors. Routes are reconstructed on demand from
/// the selected MST parents, not from BFS predecessors or arbitrary links.
/// </summary>
public sealed class MeshGatewaySpanningTreeRouting
{
    private readonly HashSet<MeshNodeKey> _registeredLookup;
    private readonly HashSet<MeshNodeKey> _unavailableLookup;

    public MeshGatewaySpanningTreeRouting(
        MinimumSpanningTreeResult tree,
        IEnumerable<MeshNodeKey> registeredSensors,
        IEnumerable<MeshNodeKey> unavailableSensors,
        IEnumerable<MeshDisconnectedComponent> disconnectedComponents)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(registeredSensors);
        ArgumentNullException.ThrowIfNull(unavailableSensors);
        ArgumentNullException.ThrowIfNull(disconnectedComponents);

        MeshNodeKey[] registered = registeredSensors.ToArray();
        MeshNodeKey[] unavailable = unavailableSensors.ToArray();
        MeshDisconnectedComponent[] components = disconnectedComponents.ToArray();

        ValidateOrderedSensorKeys(registered, "registered sensors");
        ValidateOrderedSensorKeys(unavailable, "unavailable sensors");

        _registeredLookup = new HashSet<MeshNodeKey>(registered);
        _unavailableLookup = new HashSet<MeshNodeKey>(unavailable);
        var classified = new HashSet<MeshNodeKey>();

        // A Prim result contains the gateway, then precisely one selected
        // parent branch per connected sensor. It cannot invent a new sensor.
        foreach (MeshNodeKey reached in tree.ReachedNodes)
        {
            if (reached.IsGateway)
            {
                continue;
            }

            if (!_registeredLookup.Contains(reached) || !classified.Add(reached))
            {
                throw new InvalidMeshTopologyException(
                    "The selected spanning tree must contain only registered sensors.");
            }
        }

        foreach (MeshNodeKey sensor in unavailable)
        {
            if (!_registeredLookup.Contains(sensor) || !classified.Add(sensor))
            {
                throw new InvalidMeshTopologyException(
                    "Unavailable sensors must be registered and outside the tree.");
            }
        }

        MeshNodeKey? previousComponentStart = null;
        foreach (MeshDisconnectedComponent? component in components)
        {
            if (component is null)
            {
                throw new InvalidMeshTopologyException(
                    "A spanning-tree routing summary cannot contain a null component.");
            }

            MeshNodeKey first = component.Sensors[0];
            if (previousComponentStart is not null &&
                previousComponentStart.Value.CompareTo(first) >= 0)
            {
                throw new InvalidMeshTopologyException(
                    "Disconnected components must have canonical ascending order.");
            }

            previousComponentStart = first;
            foreach (MeshNodeKey sensor in component.Sensors)
            {
                if (!_registeredLookup.Contains(sensor) || !classified.Add(sensor))
                {
                    throw new InvalidMeshTopologyException(
                        "A disconnected sensor cannot also be reached or unavailable.");
                }
            }
        }

        if (classified.Count != registered.Length)
        {
            throw new InvalidMeshTopologyException(
                "Every registered sensor requires exactly one tree-route classification.");
        }

        Tree = tree;
        RegisteredSensors = Array.AsReadOnly(registered);
        UnavailableSensors = Array.AsReadOnly(unavailable);
        DisconnectedComponents = Array.AsReadOnly(components);
    }

    public MinimumSpanningTreeResult Tree { get; }

    public long GraphVersion => Tree.GraphVersion;

    /// <summary>Sum of ALL selected tree-link weights, not a device path.</summary>
    public double TotalSimulatedEnergyCost => Tree.TotalSimulatedEnergyCost;

    public IReadOnlyList<MeshNodeKey> RegisteredSensors { get; }

    public IReadOnlyList<MeshNodeKey> UnavailableSensors { get; }

    /// <summary>
    /// Connected components of eligible sensors not connected to the gateway.
    /// Unavailable sensors are listed separately, not as false islands.
    /// </summary>
    public IReadOnlyList<MeshDisconnectedComponent> DisconnectedComponents { get; }

    public int RegisteredSensorCount => RegisteredSensors.Count;

    public int ReachableSensorCount => Tree.ReachableSensorCount;

    public int DisconnectedSensorCount =>
        RegisteredSensorCount - ReachableSensorCount - UnavailableSensorCount;

    public int UnavailableSensorCount => UnavailableSensors.Count;

    public int SelectedEdgeCount => Tree.SelectedEdgeCount;

    /// <summary>
    /// Returns true for a registered sensor even if its route status is
    /// Disconnected or Unavailable. Returns false with null for an unknown
    /// sensor. Invalid or gateway identities are rejected as programmer errors.
    /// Reconstruction costs O(h) time and memory for h selected tree links.
    /// </summary>
    public bool TryGetTreeRouteToGateway(
        MeshNodeKey sensor,
        out MeshTreeRoute? route)
    {
        if (!sensor.IsValid || sensor.IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "A tree-route lookup requires a valid registered sensor key.");
        }

        if (!_registeredLookup.Contains(sensor))
        {
            route = null;
            return false;
        }

        if (_unavailableLookup.Contains(sensor))
        {
            route = new MeshTreeRoute(GraphVersion, sensor,
                MeshRouteStatus.Unavailable, [], []);
            return true;
        }

        if (!Tree.ContainsNode(sensor))
        {
            route = new MeshTreeRoute(GraphVersion, sensor,
                MeshRouteStatus.Disconnected, [], []);
            return true;
        }

        var path = new List<MeshNodeKey> { sensor };
        var links = new List<MeshLink>();
        MeshNodeKey current = sensor;

        // MinimumSpanningTreeResult independently validates that every
        // selected parent chain terminates at the single logical gateway.
        while (!current.IsGateway)
        {
            if (!Tree.TryGetBranch(current, out MeshTreeBranch? branch) ||
                branch is null)
            {
                throw new InvalidMeshTopologyException(
                    "A reached tree sensor is unexpectedly missing its parent link.");
            }

            links.Add(branch.Link);
            current = branch.Parent;
            path.Add(current);
        }

        route = new MeshTreeRoute(GraphVersion, sensor,
            MeshRouteStatus.Reachable, path, links);
        return true;
    }

    private static void ValidateOrderedSensorKeys(
        IReadOnlyList<MeshNodeKey> sensors,
        string description)
    {
        MeshNodeKey? previous = null;
        foreach (MeshNodeKey sensor in sensors)
        {
            if (!sensor.IsValid || sensor.IsGateway ||
                (previous is not null && previous.Value.CompareTo(sensor) >= 0))
            {
                throw new InvalidMeshTopologyException(
                    $"The {description} must contain unique, ordered sensor identities.");
            }

            previous = sensor;
        }
    }
}