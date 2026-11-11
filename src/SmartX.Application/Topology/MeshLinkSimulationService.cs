using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// Application-level validation and orchestration for opt-in logical mesh
/// simulation. All actual link state is owned by IMeshTopologyStore; this
/// service never caches or duplicates a graph or sensor registry.
///
/// The HTTP boundary is responsible for checking simulation-mode access
/// BEFORE calling any mutation method.
/// </summary>
public sealed class MeshLinkSimulationService
{
    private const string SensorPrefix = "sensor/";

    private readonly IMeshTopologyStore _store;

    public MeshLinkSimulationService(IMeshTopologyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Creates a new, server-identified undirected link. MeshLink validates
    /// distinct endpoints and cost; the synchronized store validates that
    /// both sensor IDs are registered, not decommissioned, and not already
    /// connected, including under concurrent writes.
    /// </summary>
    public Task<MeshLink> CreateLinkAsync(
        string? firstNodeKey,
        string? secondNodeKey,
        double simulatedEnergyCost,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MeshNodeKey first = ParseNodeKey(firstNodeKey);
        MeshNodeKey second = ParseNodeKey(secondNodeKey);
        var link = new MeshLink(
            Guid.NewGuid(), first, second, simulatedEnergyCost);

        return _store.AddLinkAsync(link, cancellationToken);
    }

    /// <summary>
    /// Atomically removes a link from both adjacency directions. Returns
    /// false for an unknown non-empty ID so HTTP can produce 404.
    /// </summary>
    public Task<bool> RemoveLinkAsync(
        Guid linkId,
        CancellationToken cancellationToken = default) =>
        _store.RemoveLinkAsync(linkId, cancellationToken);

    /// <summary>
    /// Changes only a known link's weight. A snapshot precheck distinguishes
    /// a missing link (HTTP 404) from an invalid cost (HTTP 400). The store
    /// remains the final synchronized authority if the link changes between
    /// the precheck and the update.
    ///
    /// An equal-weight update is a no-op and preserves the graph version.
    /// </summary>
    public async Task<MeshLink?> UpdateLinkCostAsync(
        Guid linkId,
        double simulatedEnergyCost,
        CancellationToken cancellationToken = default)
    {
        MeshLinkCostUpdateOutcome outcome = await UpdateLinkCostTrackedAsync(
            linkId, simulatedEnergyCost, cancellationToken).ConfigureAwait(false);
        return outcome.Link;
    }

    /// <summary>
    /// Retains the Commit 2 update contract while reporting whether the
    /// previously observed weight differs from the successful stored result.
    /// An equal-cost PATCH must not emit a redundant topology invalidation.
    /// The store is still the only synchronized write authority; under
    /// concurrent writers this flag is advisory, and clients always GET.
    /// </summary>
    public async Task<MeshLinkCostUpdateOutcome> UpdateLinkCostTrackedAsync(
        Guid linkId,
        double simulatedEnergyCost,
        CancellationToken cancellationToken = default)
    {
        if (linkId == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A mesh link operation requires a non-empty link ID.");
        }

        if (!double.IsFinite(simulatedEnergyCost) ||
            simulatedEnergyCost <= 0d)
        {
            throw new InvalidMeshTopologyException(
                "A simulated mesh link cost must be finite and greater than zero.");
        }

        MeshGraphSnapshot snapshot = await _store
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        MeshLink? existing = snapshot.Links.FirstOrDefault(
            link => link.Id == linkId);

        if (existing is null)
        {
            return new MeshLinkCostUpdateOutcome(null, Changed: false);
        }

        MeshLink updated = await _store
            .UpdateLinkCostAsync(linkId, simulatedEnergyCost, cancellationToken)
            .ConfigureAwait(false);

        return new MeshLinkCostUpdateOutcome(
            updated,
            Changed: existing.SimulatedEnergyCost != updated.SimulatedEnergyCost);
    }

    /// <summary>
    /// Parses only the canonical node-key shapes published by the topology
    /// read API. Display names, arbitrary GUIDs and MAC addresses are never
    /// interpreted as graph identities. Unknown but well-formed sensor IDs
    /// are rejected by the authoritative store during creation.
    /// </summary>
    internal static MeshNodeKey ParseNodeKey(string? key)
    {
        if (key == MeshNodeKey.Gateway.ToString())
        {
            return MeshNodeKey.Gateway;
        }

        if (key is not null &&
            key.StartsWith(SensorPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(key.AsSpan(SensorPrefix.Length), "D", out Guid id) &&
            id != Guid.Empty)
        {
            return MeshNodeKey.ForSensor(id);
        }

        throw new InvalidMeshTopologyException(
            "A mesh endpoint must be 'gateway' or 'sensor/{registered-guid}'.");
    }
}