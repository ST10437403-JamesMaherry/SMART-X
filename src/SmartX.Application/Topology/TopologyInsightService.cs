using SmartX.Contracts.Topology;
using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// P3-10: derives bounded, explainable OPERATIONAL observations from current
/// registered mesh nodes and the existing BFS reachability algorithm.
///
/// This is deliberately independent of PredictivePatternLearningService.
/// Reads do not record searches, train models, issue relay commands, mutate
/// topology or produce SignalR events. Only the canonical graph store owns
/// topology, and BFS is calculated ONCE per changed graph revision.
///
/// A singleton instance serializes version transitions so overlapping HTTP
/// reads cannot duplicate recovery notices. It stores at most two current
/// conditions, one transient recovery, and a bounded set of IDs.
/// </summary>
public sealed class TopologyInsightService
{
    public const int MaximumAffectedIdsPerInsight = 16;
    public const int MaximumInsightsPerResponse = 3;

    public static readonly TimeSpan RecoveryVisibleFor = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(60);

    private const string DisconnectedKey = "topology:gateway-disconnected";
    private const string UnavailableKey = "topology:forwarding-unavailable";
    private const string RestoredKey = "topology:gateway-restored";

    private readonly IMeshTopologyStore _topologyStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _revisionGate = new(1, 1);

    // All state below is protected by _revisionGate. No telemetry payloads,
    // historical operator interactions, or command records are retained.
    private long _cachedVersion = -1;
    private DateTimeOffset _projectionComputedAtUtc;
    private int _registeredCount;
    private int _reachableCount;
    private int _disconnectedCount;
    private int _unavailableCount;
    private DateTimeOffset? _disconnectedFirstObservedAtUtc;
    private DateTimeOffset? _unavailableFirstObservedAtUtc;
    private DateTimeOffset? _lastRecoveryPublishedAtUtc;
    private HashSet<Guid> _previousDisconnected = new();
    private TopologyInsightResponse? _disconnected;
    private TopologyInsightResponse? _unavailable;
    private TopologyInsightResponse? _recovery;

    public TopologyInsightService(
        IMeshTopologyStore topologyStore,
        TimeProvider timeProvider)
    {
        _topologyStore = topologyStore
            ?? throw new ArgumentNullException(nameof(topologyStore));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// A first-ever read of a faulty graph reports its CURRENT fault. A
    /// recovery notice is emitted only if this service previously observed
    /// disconnected registered sensors which are now actually reachable.
    /// </summary>
    public async Task<TopologyInsightsResponse> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await _revisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolve the source under the same gate as the version cache.
            // Repeated HTTP reads still observe the registry/store revision,
            // but do not re-run the O(V+E) BFS when it is unchanged.
            MeshGraphSnapshot snapshot = await _topologyStore
                .GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();

            if (snapshot.Version != _cachedVersion)
            {
                RebuildProjection(snapshot, now, cancellationToken);
            }

            // Transient recovery evidence has a wall-clock lifetime. The
            // expiry check also works when no new graph events arrive.
            if (_recovery is not null && _recovery.ExpiresAtUtc <= now)
            {
                _recovery = null;
            }

            var current = new List<TopologyInsightResponse>(MaximumInsightsPerResponse);
            if (_disconnected is not null)
            {
                current.Add(_disconnected);
            }

            if (_unavailable is not null)
            {
                current.Add(_unavailable);
            }

            if (_recovery is not null)
            {
                current.Add(_recovery);
            }

            return new TopologyInsightsResponse(
                _cachedVersion,
                now,
                _projectionComputedAtUtc,
                _registeredCount,
                _reachableCount,
                _disconnectedCount,
                _unavailableCount,
                Array.AsReadOnly(current.ToArray()));
        }
        finally
        {
            _revisionGate.Release();
        }
    }

    private void RebuildProjection(
        MeshGraphSnapshot snapshot,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        // Existing P3-05 BFS is the only reachability source. Components
        // exclude Offline/Fault/Maintenance/Decommissioned vertices, which
        // cannot forward even when a physical-looking link is configured.
        MeshGatewayRouting routing = MeshTraversalService
            .CalculateGatewayRouting(snapshot, cancellationToken);

        var disconnectedLookup = new HashSet<MeshNodeKey>(
            routing.DisconnectedComponents.SelectMany(component => component.Sensors));
        Guid[] disconnectedIds = snapshot.Nodes
            .Where(node => !node.IsGateway && disconnectedLookup.Contains(node.Key))
            .Select(node => node.Key.SensorProfileId)
            .ToArray();

        // Only report a non-forwarding device as an operational mesh risk if
        // it has a configured connection. Newly registered, unlinked Offline
        // inventory should not generate a flood of health warnings.
        var linkedKeys = new HashSet<MeshNodeKey>();
        foreach (MeshLink link in snapshot.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            linkedKeys.Add(link.NodeA);
            linkedKeys.Add(link.NodeB);
        }

        Guid[] linkedUnavailableIds = snapshot.Nodes
            .Where(node => !node.IsGateway && !node.IsForwardingEligible &&
                           linkedKeys.Contains(node.Key))
            .Select(node => node.Key.SensorProfileId)
            .ToArray();

        // A previously disconnected sensor becoming unavailable is NOT a
        // connectivity restoration. Confirm that recovered identities are in
        // the current BFS reached set before announcing recovery.
        var reachedIds = new HashSet<Guid>(routing.Reachability.ReachableNodes
            .Where(entry => !entry.Node.IsGateway)
            .Select(entry => entry.Node.SensorProfileId));
        Guid[] recoveredIds = _previousDisconnected
            .Where(reachedIds.Contains)
            .OrderBy(id => id)
            .ToArray();

        // Perform cancellation checks before changing the cached projection.
        cancellationToken.ThrowIfCancellationRequested();

        bool hadDisconnection = _previousDisconnected.Count > 0;
        bool isDisconnected = disconnectedIds.Length > 0;
        bool wasUnavailable = _unavailable is not null;
        bool isUnavailable = linkedUnavailableIds.Length > 0;

        _disconnectedFirstObservedAtUtc = isDisconnected
            ? hadDisconnection
                ? _disconnectedFirstObservedAtUtc ?? observedAtUtc
                : observedAtUtc
            : null;

        _unavailableFirstObservedAtUtc = isUnavailable
            ? wasUnavailable
                ? _unavailableFirstObservedAtUtc ?? observedAtUtc
                : observedAtUtc
            : null;

        _disconnected = isDisconnected
            ? BuildCurrent(
                DisconnectedKey, "GatewayDisconnected",
                disconnectedIds.Length >= 2 ? "Critical" : "Warning",
                "CurrentTopologyBfs",
                "Gateway route unavailable",
                $"{disconnectedIds.Length} forwarding-eligible registered " +
                $"sensor(s) cannot reach the logical gateway across " +
                $"{routing.DisconnectedComponents.Count} disconnected " +
                $"component(s) in graph revision {snapshot.Version}.",
                "Inspect the actual configured gateway/relay links and " +
                "sensor operational states; simulate changes only in " +
                "an explicitly enabled test environment.",
                snapshot.Version,
                _disconnectedFirstObservedAtUtc!.Value,
                observedAtUtc,
                disconnectedIds)
            : null;

        _unavailable = isUnavailable
            ? BuildCurrent(
                UnavailableKey, "NonForwardingDevices", "Warning",
                "CurrentTopologySensorStatus",
                "Configured sensor cannot forward",
                $"{linkedUnavailableIds.Length} registered sensor(s) with " +
                "configured links are in non-forwarding operational states " +
                $"in graph revision {snapshot.Version}. Configured links " +
                "remain present; they are not valid forwarding paths.",
                "Inspect the device's reported status and configured " +
                "connections before considering a validated operator action.",
                snapshot.Version,
                _unavailableFirstObservedAtUtc!.Value,
                observedAtUtc,
                linkedUnavailableIds)
            : null;

        // Active disconnection takes priority over a celebratory message.
        // The recovery notice is short-lived and rate-limited for flapping
        // links. No active safety concern is suppressed by this cooldown.
        if (isDisconnected)
        {
            _recovery = null;
        }
        else if (hadDisconnection && recoveredIds.Length > 0)
        {
            bool cooledDown = _lastRecoveryPublishedAtUtc is null ||
                observedAtUtc - _lastRecoveryPublishedAtUtc.Value >= RecoveryCooldown;

            _recovery = cooledDown
                ? BuildRecovery(snapshot.Version, observedAtUtc, recoveredIds)
                : null;

            if (cooledDown)
            {
                _lastRecoveryPublishedAtUtc = observedAtUtc;
            }
        }
        else if (_recovery is not null)
        {
            // An unrelated graph edit must not carry a recovery notice for a
            // device that is no longer reachable, even before its TTL ends.
            bool stillRecovered = _recovery.AffectedSensorIds.All(reachedIds.Contains);
            if (!stillRecovered)
            {
                _recovery = null;
            }
        }

        // Store only real, registered identities; a new graph revision cannot
        // bring fabricated nodes into the alert history or payload.
        _previousDisconnected = new HashSet<Guid>(disconnectedIds);
        _registeredCount = routing.RegisteredSensorCount;
        _reachableCount = routing.ReachableSensorCount;
        _disconnectedCount = routing.DisconnectedSensorCount;
        _unavailableCount = routing.UnavailableSensorCount;
        _projectionComputedAtUtc = observedAtUtc;
        _cachedVersion = snapshot.Version;
    }

    private static TopologyInsightResponse BuildCurrent(
        string key,
        string kind,
        string severity,
        string source,
        string title,
        string explanation,
        string recommendedAction,
        long version,
        DateTimeOffset firstObservedAtUtc,
        DateTimeOffset lastConfirmedAtUtc,
        Guid[] affected)
    {
        Guid[] bounded = affected.Take(MaximumAffectedIdsPerInsight).ToArray();
        return new TopologyInsightResponse(
            key, kind, severity, source,
            false, true,
            title, explanation, recommendedAction,
            version, firstObservedAtUtc, lastConfirmedAtUtc,
            null,
            affected.Length, Array.AsReadOnly(bounded),
            affected.Length - bounded.Length);
    }

    private static TopologyInsightResponse BuildRecovery(
        long version,
        DateTimeOffset observedAtUtc,
        Guid[] recovered)
    {
        Guid[] bounded = recovered.Take(MaximumAffectedIdsPerInsight).ToArray();
        return new TopologyInsightResponse(
            RestoredKey, "GatewayConnectivityRestored", "Info",
            "ObservedTopologyTransition",
            false, false,
            "Gateway connectivity restored",
            $"{recovered.Length} previously gateway-disconnected registered " +
            $"sensor(s) are now reachable in graph revision {version}. " +
            "This observation is not a prediction or a command result.",
            "Review the current gateway path and continue monitoring " +
            "device health; no action has been executed automatically.",
            version, observedAtUtc, observedAtUtc,
            observedAtUtc.Add(RecoveryVisibleFor),
            recovered.Length, Array.AsReadOnly(bounded),
            recovered.Length - bounded.Length);
    }
}