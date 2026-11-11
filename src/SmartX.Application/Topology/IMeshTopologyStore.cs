using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// Application boundary for the process-local, simulated IoT mesh.
/// Sensor membership and status always come from <c>ISensorRepository</c>;
/// this abstraction stores only the explicitly configured radio links.
/// </summary>
public interface IMeshTopologyStore
{
    /// <summary>
    /// Returns an immutable, deterministically ordered graph revision.
    /// Refreshes sensor membership and health before reading the graph.
    /// An empty registry still produces the single logical gateway.
    /// </summary>
    Task<MeshGraphSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns each undirected link touching a known vertex once, ordered
    /// by its opposite endpoint. A missing vertex is a validation error.
    /// </summary>
    Task<IReadOnlyList<MeshLink>> GetIncidentLinksAsync(
        MeshNodeKey node,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an explicitly simulated link. Both endpoints must exist in the
    /// authoritative registry (or be the logical gateway), must differ,
    /// and must not be decommissioned. Link IDs and unordered pairs are unique.
    /// Offline sensors may be inventoried and linked but cannot forward traffic.
    /// </summary>
    Task<MeshLink> AddLinkAsync(
        MeshLink link,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one link and both adjacency directions together. Returns
    /// false when the non-empty link ID is not present.
    /// </summary>
    Task<bool> RemoveLinkAsync(
        Guid linkId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes only a link's simulated energy cost, preserving its ID and
    /// endpoints. Throws for unknown IDs or non-positive/non-finite costs.
    /// A no-op update does not advance the graph revision.
    /// </summary>
    Task<MeshLink> UpdateLinkCostAsync(
        Guid linkId,
        double simulatedEnergyCost,
        CancellationToken cancellationToken = default);
}