namespace SmartX.Domain.Topology;

/// <summary>
/// Explicit gateway-connectivity outcome for a registered sensor. Shared by
/// BFS fewest-hop routes (P3-05) and selected Prim tree routes (P3-06).
/// Unregistered identities are NOT a status: each routing projection exposes
/// a TryGet method that returns false for unknown sensor identities.
/// </summary>
public enum MeshRouteStatus : byte
{
    /// <summary>A forwarding-eligible sensor has a configured gateway path.</summary>
    Reachable = 1,

    /// <summary>
    /// The sensor can forward, but no chain of forwarding-eligible configured
    /// links connects its component to the gateway.
    /// </summary>
    Disconnected = 2,

    /// <summary>
    /// The registered sensor's health status currently prohibits forwarding.
    /// Its configured links may still exist and are not the cause reported here.
    /// </summary>
    Unavailable = 3
}