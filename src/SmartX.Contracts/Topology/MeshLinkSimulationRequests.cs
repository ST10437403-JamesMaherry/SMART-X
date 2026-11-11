namespace SmartX.Contracts.Topology;

/// <summary>
/// Requests one explicitly configured, UNDIRECTED simulation link.
/// Node keys follow the existing GET /api/topology representation:
/// "gateway" or "sensor/{registered-sensor-guid}".
/// No client-supplied link ID is accepted: the server assigns it.
/// </summary>
public sealed record CreateMeshLinkRequest(
    string? NodeAKey,
    string? NodeBKey,
    double SimulatedEnergyCost);

/// <summary>
/// Updates the relative, simulated cost of an existing link without
/// changing its stable link ID or either endpoint.
/// Costs are positive and finite; they are not physical energy readings.
/// </summary>
public sealed record UpdateMeshLinkCostRequest(double SimulatedEnergyCost);