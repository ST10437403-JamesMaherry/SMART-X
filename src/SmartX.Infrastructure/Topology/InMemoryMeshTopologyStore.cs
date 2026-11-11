using SmartX.Application.Sensors;
using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Infrastructure.Topology;

/// <summary>
/// In-memory, undirected adjacency-list representation of the simulated
/// Smart-X radio mesh. One shared gate serialises registry refresh, mutation
/// and snapshot construction, so consumers never observe half an edge.
///
/// Registered sensors are the ONLY authoritative sensor inventory. A newly
/// registered sensor becomes an isolated vertex on the next mesh operation;
/// registration never invents a gateway or sensor-to-sensor radio link.
/// This is simulation state, not discovery of actual physical connections.
/// </summary>
public sealed class InMemoryMeshTopologyStore : IMeshTopologyStore
{
    private readonly ISensorRepository _sensorRepository;

    // SemaphoreSlim permits asynchronous registry reads without awaiting
    // inside a lock. All the following dictionaries are gate-protected.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<MeshNodeKey, MeshNode> _nodes = new()
    {
        [MeshNodeKey.Gateway] = MeshNode.Gateway
    };

    // Both directions contain the SAME immutable MeshLink object. Thus the
    // graph has O(V + E) storage; a link is not duplicated in snapshots.
    private readonly Dictionary<MeshNodeKey, Dictionary<MeshNodeKey, MeshLink>>
        _adjacency = new()
        {
            [MeshNodeKey.Gateway] = new Dictionary<MeshNodeKey, MeshLink>()
        };

    // Canonical single-entry index enables O(1) expected link-ID operations.
    private readonly Dictionary<Guid, MeshLink> _linksById = new();

    private long _version;

    public InMemoryMeshTopologyStore(ISensorRepository sensorRepository)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));
    }

    /// <inheritdoc />
    public Task<MeshGraphSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteWithRegistryAsync(
            () => new MeshGraphSnapshot(
                _version,
                _nodes.Values,
                _linksById.Values),
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MeshLink>> GetIncidentLinksAsync(
        MeshNodeKey node,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(node);

        return ExecuteWithRegistryAsync<IReadOnlyList<MeshLink>>(
            () =>
            {
                EnsureKnownNode(node);

                // Sorting by neighbouring key, rather than hash iteration
                // order, stabilises future BFS and same-weight MST tie breaks.
                MeshLink[] sorted = _adjacency[node]
                    .OrderBy(entry => entry.Key)
                    .Select(entry => entry.Value)
                    .ToArray();

                return Array.AsReadOnly(sorted);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MeshLink> AddLinkAsync(
        MeshLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        return ExecuteWithRegistryAsync(
            () =>
            {
                EnsureEligibleEndpoint(link.NodeA);
                EnsureEligibleEndpoint(link.NodeB);

                if (_linksById.ContainsKey(link.Id))
                {
                    throw new InvalidMeshTopologyException(
                        "A mesh link with this ID already exists.");
                }

                // MeshLink canonicalises the unordered endpoints. Looking up
                // A->B is enough to detect either proposed orientation.
                if (_adjacency[link.NodeA].ContainsKey(link.NodeB))
                {
                    throw new InvalidMeshTopologyException(
                        "These mesh vertices are already connected by a link.");
                }

                _linksById.Add(link.Id, link);
                _adjacency[link.NodeA].Add(link.NodeB, link);
                _adjacency[link.NodeB].Add(link.NodeA, link);
                AdvanceVersion();
                return link;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveLinkAsync(
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        ValidateLinkId(linkId);

        return ExecuteWithRegistryAsync(
            () =>
            {
                if (!_linksById.TryGetValue(linkId, out MeshLink? link))
                {
                    return false;
                }

                RemoveLinkCore(link);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MeshLink> UpdateLinkCostAsync(
        Guid linkId,
        double simulatedEnergyCost,
        CancellationToken cancellationToken = default)
    {
        ValidateLinkId(linkId);

        return ExecuteWithRegistryAsync(
            () =>
            {
                if (!_linksById.TryGetValue(linkId, out MeshLink? existing))
                {
                    throw new InvalidMeshTopologyException(
                        "The requested mesh link does not exist.");
                }

                // The immutable domain factory validates positive, finite
                // weights before touching any mutable store state.
                MeshLink replacement =
                    existing.WithSimulatedEnergyCost(simulatedEnergyCost);

                if (existing.SimulatedEnergyCost == simulatedEnergyCost)
                {
                    return existing;
                }

                _linksById[linkId] = replacement;
                _adjacency[replacement.NodeA][replacement.NodeB] = replacement;
                _adjacency[replacement.NodeB][replacement.NodeA] = replacement;
                AdvanceVersion();
                return replacement;
            },
            cancellationToken);
    }

    /// <summary>
    /// Serialises the complete operation, INCLUDING the registry read.
    /// This prevents an older, slower registry response from overwriting a
    /// newer topology projection if calls arrive concurrently.
    /// No async I/O occurs during dictionary mutation or snapshot creation.
    /// </summary>
    private async Task<TResult> ExecuteWithRegistryAsync<TResult>(
        Func<TResult> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IReadOnlyList<SensorProfile> registeredSensors =
                await _sensorRepository.GetAllAsync(cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            ReconcileSensorInventory(registeredSensors);

            return operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds sensor vertex projections from real registered profiles.
    /// Offline, Warning, Fault and Maintenance nodes remain visible; later
    /// BFS/MST services will use MeshNode.IsForwardingEligible to exclude
    /// non-forwarding devices from routing.
    ///
    /// Decommissioned sensors remain inventoried but cannot retain links.
    /// Should a future registry support deletion, absent vertices and their
    /// incident links are also removed. No connection is added implicitly.
    /// </summary>
    private void ReconcileSensorInventory(
        IReadOnlyList<SensorProfile> registeredSensors)
    {
        // Validate/project the complete registry BEFORE changing graph state.
        // A malformed registry response cannot partially replace inventory.
        var authoritative = new Dictionary<MeshNodeKey, MeshNode>();

        foreach (SensorProfile profile in registeredSensors)
        {
            MeshNode node = MeshNode.FromSensor(profile);

            if (!authoritative.TryAdd(node.Key, node))
            {
                throw new InvalidMeshTopologyException(
                    "The sensor registry returned a duplicate profile identity.");
            }
        }

        // Drop disappeared profiles, if a future registry adds deletion.
        MeshNodeKey[] removed = _nodes.Keys
            .Where(key => !key.IsGateway && !authoritative.ContainsKey(key))
            .ToArray();

        foreach (MeshNodeKey key in removed)
        {
            RemoveIncidentLinksCore(key);
            _adjacency.Remove(key);
            _nodes.Remove(key);
            AdvanceVersion();
        }

        foreach (MeshNode node in authoritative.Values)
        {
            if (_nodes.TryGetValue(node.Key, out MeshNode? existing))
            {
                if (existing.Status != node.Status)
                {
                    _nodes[node.Key] = node;
                    AdvanceVersion();
                }
            }
            else
            {
                // A legitimate new sensor is an isolated vertex until a
                // simulation operator explicitly configures a link.
                _nodes.Add(node.Key, node);
                _adjacency.Add(node.Key, new Dictionary<MeshNodeKey, MeshLink>());
                AdvanceVersion();
            }

            if (node.Status == SensorStatus.Decommissioned)
            {
                RemoveIncidentLinksCore(node.Key);
            }
        }
    }

    private void RemoveIncidentLinksCore(MeshNodeKey node)
    {
        // Copy once: RemoveLinkCore mutates both adjacency dictionaries.
        foreach (MeshLink link in _adjacency[node].Values.ToArray())
        {
            RemoveLinkCore(link);
        }
    }

    private void RemoveLinkCore(MeshLink link)
    {
        _linksById.Remove(link.Id);
        _adjacency[link.NodeA].Remove(link.NodeB);
        _adjacency[link.NodeB].Remove(link.NodeA);
        AdvanceVersion();
    }

    private void EnsureEligibleEndpoint(MeshNodeKey key)
    {
        EnsureKnownNode(key);

        if (_nodes[key].Status == SensorStatus.Decommissioned)
        {
            throw new InvalidMeshTopologyException(
                "A decommissioned sensor cannot be connected in the mesh.");
        }
    }

    private void EnsureKnownNode(MeshNodeKey key)
    {
        if (!_nodes.ContainsKey(key))
        {
            throw new InvalidMeshTopologyException(
                "The mesh vertex must be a registered sensor or the gateway.");
        }
    }

    private static void ValidateKey(MeshNodeKey key)
    {
        if (!key.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "The mesh vertex identity is invalid.");
        }
    }

    private static void ValidateLinkId(Guid linkId)
    {
        if (linkId == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A mesh link operation requires a non-empty link ID.");
        }
    }

    private void AdvanceVersion() => _version = checked(_version + 1);
}