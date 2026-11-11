namespace SmartX.Contracts.Topology;

/// <summary>
/// BFS connectivity for one graph revision. ReachableNodes includes the
/// gateway with zero hops; unavailable sensors are NEVER treated as relays.
/// </summary>
public sealed record MeshReachabilityResponse(
    long GraphVersion,
    string GatewayKey,
    int RegisteredSensorCount,
    int ReachableSensorCount,
    int DisconnectedSensorCount,
    int UnavailableSensorCount,
    IReadOnlyList<MeshHopCountResponse> ReachableNodes,
    IReadOnlyList<string> UnavailableSensorKeys,
    IReadOnlyList<MeshDisconnectedComponentResponse> DisconnectedComponents);

/// <summary>
/// Minimum number of configured links from the gateway in an unweighted BFS.
/// </summary>
public sealed record MeshHopCountResponse(
    string NodeKey,
    int HopCount);

/// <summary>
/// One gateway-disconnected component containing only forwarding-eligible
/// registered sensors. Failed or retired sensors are reported separately.
/// </summary>
public sealed record MeshDisconnectedComponentResponse(
    IReadOnlyList<string> SensorKeys);