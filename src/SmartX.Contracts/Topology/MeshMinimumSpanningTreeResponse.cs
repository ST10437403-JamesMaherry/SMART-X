namespace SmartX.Contracts.Topology;

/// <summary>
/// Gateway-rooted Prim backbone over one snapshot. TotalSimulatedEnergyCost
/// is the SUM of selected edge weights, not the cost of an individual route.
/// </summary>
public sealed record MeshMinimumSpanningTreeResponse(
    long GraphVersion,
    string GatewayKey,
    double TotalSimulatedEnergyCost,
    int RegisteredSensorCount,
    int ReachableSensorCount,
    int DisconnectedSensorCount,
    int UnavailableSensorCount,
    int SelectedEdgeCount,
    IReadOnlyList<string> ReachedNodeKeys,
    IReadOnlyList<MeshSelectedTreeEdgeResponse> SelectedEdges,
    IReadOnlyList<string> UnavailableSensorKeys,
    IReadOnlyList<MeshDisconnectedComponentResponse> DisconnectedComponents);

/// <summary>
/// Selected undirected edge and its directed parent/child orientation in the
/// gateway-rooted MST. Only configured links can appear here.
/// </summary>
public sealed record MeshSelectedTreeEdgeResponse(
    Guid LinkId,
    string ParentNodeKey,
    string ChildNodeKey,
    double SimulatedEnergyCost);