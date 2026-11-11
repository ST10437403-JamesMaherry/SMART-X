namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable gateway route following only links selected by Prim's spanning
/// tree. Unlike a P3-05 BFS route, this path need not use the fewest hops;
/// unlike a shortest-weighted-path algorithm, its accumulated cost is not
/// necessarily the cheapest possible cost for this particular sensor.
/// </summary>
public sealed class MeshTreeRoute
{
    public MeshTreeRoute(
        long graphVersion,
        MeshNodeKey sensor,
        MeshRouteStatus status,
        IEnumerable<MeshNodeKey> pathToGateway,
        IEnumerable<MeshLink> selectedLinksToGateway)
    {
        if (graphVersion < 0)
        {
            throw new InvalidMeshTopologyException(
                "A spanning-tree route cannot reference a negative graph revision.");
        }

        if (!sensor.IsValid || sensor.IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "A spanning-tree route requires a valid sensor identity.");
        }

        if (!Enum.IsDefined(typeof(MeshRouteStatus), status))
        {
            throw new InvalidMeshTopologyException(
                "The spanning-tree route has an unsupported connectivity status.");
        }

        ArgumentNullException.ThrowIfNull(pathToGateway);
        ArgumentNullException.ThrowIfNull(selectedLinksToGateway);

        MeshNodeKey[] path = pathToGateway.ToArray();
        MeshLink[] links = selectedLinksToGateway.ToArray();
        double pathCost = 0d;

        if (status == MeshRouteStatus.Reachable)
        {
            if (path.Length < 2 || path[0] != sensor ||
                path[^1] != MeshNodeKey.Gateway ||
                links.Length != path.Length - 1 ||
                path.Any(key => !key.IsValid) ||
                path.Distinct().Count() != path.Length)
            {
                throw new InvalidMeshTopologyException(
                    "A reachable tree route requires a simple sensor-to-gateway path " +
                    "with exactly one selected link per hop.");
            }

            for (int index = 0; index < links.Length; index++)
            {
                MeshLink? link = links[index];
                if (link is null || !link.Connects(path[index], path[index + 1]))
                {
                    throw new InvalidMeshTopologyException(
                        "Every tree-route link must join its consecutive path vertices.");
                }

                // MeshLink already enforces a positive finite cost. Also
                // reject overflow when several individually valid costs sum.
                pathCost += link.SimulatedEnergyCost;
                if (!double.IsFinite(pathCost))
                {
                    throw new InvalidMeshTopologyException(
                        "The accumulated tree-route cost must remain finite.");
                }
            }
        }
        else if (path.Length != 0 || links.Length != 0)
        {
            throw new InvalidMeshTopologyException(
                "Disconnected and unavailable sensors cannot publish a partial tree route.");
        }

        GraphVersion = graphVersion;
        Sensor = sensor;
        Status = status;
        PathToGateway = Array.AsReadOnly(path);
        SelectedLinksToGateway = Array.AsReadOnly(links);
        HopCount = status == MeshRouteStatus.Reachable ? links.Length : null;
        AccumulatedSimulatedEnergyCost =
            status == MeshRouteStatus.Reachable ? pathCost : null;
    }

    public long GraphVersion { get; }

    public MeshNodeKey Sensor { get; }

    public MeshRouteStatus Status { get; }

    /// <summary>
    /// Selected tree vertices in sensor-to-gateway order. Empty if there is
    /// no route; never fabricated for a disconnected or unavailable device.
    /// </summary>
    public IReadOnlyList<MeshNodeKey> PathToGateway { get; }

    /// <summary>
    /// Original configured undirected tree links, one per path hop, ordered
    /// in the same sensor-to-gateway direction as PathToGateway.
    /// </summary>
    public IReadOnlyList<MeshLink> SelectedLinksToGateway { get; }

    /// <summary>Null if the registered sensor cannot reach the gateway.</summary>
    public int? HopCount { get; }

    /// <summary>
    /// Sum of the selected path links' relative simulated costs, or null for
    /// an unreachable sensor. This is not the total backbone cost and not a
    /// guaranteed shortest-weighted-path result.
    /// </summary>
    public double? AccumulatedSimulatedEnergyCost { get; }
}