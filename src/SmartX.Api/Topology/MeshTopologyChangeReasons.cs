namespace SmartX.Api.Topology;

/// <summary>
/// Stable reasons for lightweight topology invalidations published using the
/// existing RealtimeChanged SignalR event on /hubs/realtime.
///
/// Sensor registration, status changes and metadata updates already publish
/// the established sensor-* reasons through DashboardEventCoordinator.
/// Keep those messages rather than introducing a competing status service.
/// </summary>
public static class MeshTopologyChangeReasons
{
    public const string LinkCreated = "topology-link-created";

    public const string LinkCostChanged = "topology-link-cost-changed";

    public const string LinkRemoved = "topology-link-removed";
}