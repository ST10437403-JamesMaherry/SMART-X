namespace SmartX.Domain.Topology;

/// <summary>
/// Explicit outcome for a registered sensor's BFS route to the gateway.
/// Unregistered sensor identities are not represented by this enum: callers
/// can distinguish them using MeshGatewayRouting.TryGetRouteToGateway.
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