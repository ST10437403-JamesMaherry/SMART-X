namespace SmartX.Contracts.Topology;

/// <summary>
/// Two DIFFERENT route strategies computed from the SAME graph revision.
/// Missing sensor registrations yield HTTP 404 instead of a fake route.
/// </summary>
public sealed record MeshDeviceRouteResponse(
    long GraphVersion,
    Guid SensorProfileId,
    string Status,
    MeshHopRouteResponse Bfs,
    MeshTreeRouteResponse Mst);

/// <summary>
/// Unweighted fewest-hop sensor-to-gateway path, or an empty path when the
/// registered sensor is Disconnected or Unavailable.
/// </summary>
public sealed record MeshHopRouteResponse(
    string Status,
    int? HopCount,
    IReadOnlyList<string> PathToGateway);

/// <summary>
/// Path along Prim's selected backbone. AccumulatedSimulatedEnergyCost
/// describes this tree path ONLY; Prim does not promise a shortest-weighted
/// path for each sensor.
/// </summary>
public sealed record MeshTreeRouteResponse(
    string Status,
    int? HopCount,
    double? AccumulatedSimulatedEnergyCost,
    IReadOnlyList<string> PathToGateway,
    IReadOnlyList<Guid> SelectedLinkIdsToGateway);