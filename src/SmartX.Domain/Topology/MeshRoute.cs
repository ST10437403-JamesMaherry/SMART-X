namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable, versioned BFS path from a registered sensor toward the gateway.
/// The path is in sensor-to-gateway order and measures configured link hops,
/// not simulated energy cost. A failed route has no invented partial path.
/// </summary>
public sealed class MeshRoute
{
    public MeshRoute(
        long graphVersion,
        MeshNodeKey sensor,
        MeshRouteStatus status,
        IEnumerable<MeshNodeKey> pathToGateway)
    {
        if (graphVersion < 0)
        {
            throw new InvalidMeshTopologyException(
                "A mesh route cannot reference a negative graph version.");
        }

        if (!sensor.IsValid || sensor.IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "A mesh route must start from a valid sensor identity.");
        }

        if (!Enum.IsDefined(typeof(MeshRouteStatus), status))
        {
            throw new InvalidMeshTopologyException(
                "The mesh route has an unsupported reachability status.");
        }

        ArgumentNullException.ThrowIfNull(pathToGateway);
        MeshNodeKey[] path = pathToGateway.ToArray();

        if (status == MeshRouteStatus.Reachable)
        {
            if (path.Length < 2 || path[0] != sensor ||
                path[^1] != MeshNodeKey.Gateway ||
                path.Any(key => !key.IsValid) ||
                path.Distinct().Count() != path.Length)
            {
                throw new InvalidMeshTopologyException(
                    "A reachable mesh route must be a simple sensor-to-gateway path.");
            }
        }
        else if (path.Length != 0)
        {
            throw new InvalidMeshTopologyException(
                "An unavailable or disconnected sensor cannot publish a gateway path.");
        }

        GraphVersion = graphVersion;
        Sensor = sensor;
        Status = status;
        PathToGateway = Array.AsReadOnly(path);
        HopCount = status == MeshRouteStatus.Reachable ? path.Length - 1 : null;
    }

    public long GraphVersion { get; }

    public MeshNodeKey Sensor { get; }

    public MeshRouteStatus Status { get; }

    /// <summary>
    /// Exactly one immutable sensor-to-gateway path for a reachable device;
    /// empty for disconnected/unavailable devices.
    /// </summary>
    public IReadOnlyList<MeshNodeKey> PathToGateway { get; }

    /// <summary>
    /// Number of links along this path. Null for devices without a route.
    /// </summary>
    public int? HopCount { get; }
}