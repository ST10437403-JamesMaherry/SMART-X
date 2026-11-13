namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable classification of every registered sensor in one graph revision.
/// The BFS predecessor tree is copied privately: individual gateway paths are
/// reconstructed on demand, avoiding O(V^2) stored paths for a long chain.
/// A missing registration differs from a registered but unavailable sensor.
/// </summary>
public sealed class MeshGatewayRouting
{
    private readonly HashSet<MeshNodeKey> _registeredLookup;
    private readonly HashSet<MeshNodeKey> _unavailableLookup;
    private readonly Dictionary<MeshNodeKey, MeshNodeKey> _predecessors;

    public MeshGatewayRouting(
        MeshGatewayReachability reachability,
        IEnumerable<MeshNodeKey> registeredSensors,
        IEnumerable<MeshNodeKey> unavailableSensors,
        IEnumerable<MeshDisconnectedComponent> disconnectedComponents,
        IReadOnlyDictionary<MeshNodeKey, MeshNodeKey> predecessors)
    {
        ArgumentNullException.ThrowIfNull(reachability);
        ArgumentNullException.ThrowIfNull(registeredSensors);
        ArgumentNullException.ThrowIfNull(unavailableSensors);
        ArgumentNullException.ThrowIfNull(disconnectedComponents);
        ArgumentNullException.ThrowIfNull(predecessors);

        MeshNodeKey[] registered = registeredSensors.ToArray();
        MeshNodeKey[] unavailable = unavailableSensors.ToArray();
        MeshDisconnectedComponent[] components = disconnectedComponents.ToArray();

        ValidateOrderedSensorKeys(registered, "registered sensors");
        ValidateOrderedSensorKeys(unavailable, "unavailable sensors");

        _registeredLookup = new HashSet<MeshNodeKey>(registered);
        _unavailableLookup = new HashSet<MeshNodeKey>(unavailable);
        _predecessors = new Dictionary<MeshNodeKey, MeshNodeKey>(predecessors);

        // Partition every registered sensor exactly once into reachable,
        // unavailable, or an eligible disconnected component.
        var classified = new HashSet<MeshNodeKey>();
        foreach (MeshNodeHopCount reachable in reachability.ReachableNodes)
        {
            if (reachable.Node.IsGateway)
            {
                continue;
            }

            if (!_registeredLookup.Contains(reachable.Node) ||
                !classified.Add(reachable.Node))
            {
                throw new InvalidMeshTopologyException(
                    "Gateway reachability refers to an unregistered or repeated sensor.");
            }
        }

        foreach (MeshNodeKey sensor in unavailable)
        {
            if (!_registeredLookup.Contains(sensor) || !classified.Add(sensor))
            {
                throw new InvalidMeshTopologyException(
                    "Unavailable sensors must be registered and non-reachable.");
            }
        }

        MeshNodeKey? previousComponentStart = null;
        foreach (MeshDisconnectedComponent? component in components)
        {
            if (component is null)
            {
                throw new InvalidMeshTopologyException(
                    "A routing result cannot contain a null component.");
            }

            MeshNodeKey first = component.Sensors[0];
            if (previousComponentStart is not null &&
                previousComponentStart.Value.CompareTo(first) >= 0)
            {
                throw new InvalidMeshTopologyException(
                    "Disconnected components must be ordered by their first sensor.");
            }

            previousComponentStart = first;
            foreach (MeshNodeKey sensor in component.Sensors)
            {
                if (!_registeredLookup.Contains(sensor) || !classified.Add(sensor))
                {
                    throw new InvalidMeshTopologyException(
                        "Disconnected components must partition registered eligible sensors.");
                }
            }
        }

        if (classified.Count != registered.Length)
        {
            throw new InvalidMeshTopologyException(
                "Every registered sensor must have exactly one route classification.");
        }

        // A reachable non-gateway node has exactly one BFS predecessor with
        // a hop count one less than its own. This also rejects parent cycles.
        if (_predecessors.Count != reachability.ReachableSensorCount)
        {
            throw new InvalidMeshTopologyException(
                "The predecessor map must contain one parent per reachable sensor.");
        }

        foreach (MeshNodeHopCount node in reachability.ReachableNodes)
        {
            if (node.Node.IsGateway)
            {
                continue;
            }

            if (!_predecessors.TryGetValue(node.Node, out MeshNodeKey parent) ||
                !reachability.TryGetHopCount(parent, out int parentHops) ||
                parentHops != node.HopCount - 1)
            {
                throw new InvalidMeshTopologyException(
                    "A BFS parent must be reachable and exactly one hop nearer the gateway.");
            }
        }

        foreach (MeshNodeKey child in _predecessors.Keys)
        {
            if (!reachability.TryGetHopCount(child, out _) || child.IsGateway)
            {
                throw new InvalidMeshTopologyException(
                    "Only reachable sensors may have BFS predecessors.");
            }
        }

        Reachability = reachability;
        RegisteredSensors = Array.AsReadOnly(registered);
        UnavailableSensors = Array.AsReadOnly(unavailable);
        DisconnectedComponents = Array.AsReadOnly(components);
    }

    public long GraphVersion => Reachability.GraphVersion;

    public MeshGatewayReachability Reachability { get; }

    public IReadOnlyList<MeshNodeKey> RegisteredSensors { get; }

    public IReadOnlyList<MeshNodeKey> UnavailableSensors { get; }

    /// <summary>
    /// Only forwarding-eligible components disconnected from the gateway.
    /// All unavailable sensors are listed separately.
    /// </summary>
    public IReadOnlyList<MeshDisconnectedComponent> DisconnectedComponents { get; }

    public int RegisteredSensorCount => RegisteredSensors.Count;

    public int ReachableSensorCount => Reachability.ReachableSensorCount;

    public int UnavailableSensorCount => UnavailableSensors.Count;

    public int DisconnectedSensorCount =>
        RegisteredSensorCount - ReachableSensorCount - UnavailableSensorCount;

    /// <summary>
    /// Reconstruct one sensor-to-gateway path in O(hops) using the copied
    /// BFS predecessor tree. Returns false only for an unregistered sensor;
    /// unavailable and disconnected registered sensors return typed routes.
    /// </summary>
    public bool TryGetRouteToGateway(MeshNodeKey sensor, out MeshRoute? route)
    {
        if (!sensor.IsValid || sensor.IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "A route lookup requires a valid registered sensor key.");
        }

        if (!_registeredLookup.Contains(sensor))
        {
            route = null;
            return false;
        }

        if (_unavailableLookup.Contains(sensor))
        {
            route = new MeshRoute(GraphVersion, sensor,
                MeshRouteStatus.Unavailable, Array.Empty<MeshNodeKey>());
            return true;
        }

        if (!Reachability.TryGetHopCount(sensor, out int hopCount))
        {
            route = new MeshRoute(GraphVersion, sensor,
                MeshRouteStatus.Disconnected, Array.Empty<MeshNodeKey>());
            return true;
        }

        var path = new MeshNodeKey[checked(hopCount + 1)];
        MeshNodeKey current = sensor;
        for (int index = 0; index < hopCount; index++)
        {
            path[index] = current;
            current = _predecessors[current];
        }

        path[^1] = current;
        route = new MeshRoute(GraphVersion, sensor,
            MeshRouteStatus.Reachable, path);
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
                    $"The {description} must be unique, valid and ordered by identity.");
            }

            previous = sensor;
        }
    }
}