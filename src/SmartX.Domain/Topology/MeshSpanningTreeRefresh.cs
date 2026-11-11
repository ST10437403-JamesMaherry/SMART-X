namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable before/after view of one explicit Prim refresh. The previous
/// result remains untouched; Current is computed from a single newer graph
/// snapshot (or reuses Previous if its version is unchanged).
///
/// A refresh is a read-only simulation projection. It never modifies sensor
/// status, creates a missing radio link, or changes the physical network.
/// </summary>
public sealed class MeshSpanningTreeRefresh
{
    public MeshSpanningTreeRefresh(
        MeshGatewaySpanningTreeRouting previous,
        MeshGatewaySpanningTreeRouting current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (current.GraphVersion < previous.GraphVersion)
        {
            throw new InvalidMeshTopologyException(
                "A tree refresh cannot move backwards to an older graph revision.");
        }

        Previous = previous;
        Current = current;

        HasBackboneChanged = !SameBackbone(previous.Tree, current.Tree);
        HasDisconnectedComponentsChanged = !SameComponents(
            previous.DisconnectedComponents, current.DisconnectedComponents);

        bool sameRegistered =
            previous.RegisteredSensors.SequenceEqual(current.RegisteredSensors);
        bool sameUnavailable =
            previous.UnavailableSensors.SequenceEqual(current.UnavailableSensors);

        // The same process-local graph revision cannot describe two different
        // connectivity projections. Reject inconsistent caller-provided data
        // instead of reporting a false refresh or silently accepting stale state.
        if (previous.GraphVersion == current.GraphVersion &&
            (HasBackboneChanged || HasDisconnectedComponentsChanged ||
             !sameRegistered || !sameUnavailable))
        {
            throw new InvalidMeshTopologyException(
                "Identical graph revisions must have identical tree projections.");
        }

        // A modified but unselected edge (or an eligible status change from
        // Online to Warning) advances the graph version without changing any
        // tree route. Avoid O(sum of path lengths) reconstruction then.
        bool routeMembershipCouldChange = HasBackboneChanged ||
            !sameRegistered || !sameUnavailable;

        var transitions = new List<MeshTreeRouteTransition>();
        if (routeMembershipCouldChange)
        {
            MeshNodeKey[] allRegistered = previous.RegisteredSensors
                .Concat(current.RegisteredSensors)
                .Distinct()
                .OrderBy(sensor => sensor)
                .ToArray();

            foreach (MeshNodeKey sensor in allRegistered)
            {
                previous.TryGetTreeRouteToGateway(sensor, out MeshTreeRoute? oldRoute);
                current.TryGetTreeRouteToGateway(sensor, out MeshTreeRoute? newRoute);

                if (!MeshTreeRouteTransition.SameRoute(oldRoute, newRoute))
                {
                    transitions.Add(new MeshTreeRouteTransition(
                        sensor, oldRoute, newRoute));
                }
            }
        }

        RouteTransitions = Array.AsReadOnly(transitions.ToArray());
        ReroutedSensors = Array.AsReadOnly(transitions
            .Where(change => change.WasRerouted)
            .Select(change => change.Sensor).ToArray());
        LostGatewayConnectivitySensors = Array.AsReadOnly(transitions
            .Where(change => change.LostGatewayConnectivity)
            .Select(change => change.Sensor).ToArray());
        RegainedGatewayConnectivitySensors = Array.AsReadOnly(transitions
            .Where(change => change.RegainedGatewayConnectivity)
            .Select(change => change.Sensor).ToArray());
        NewlyRegisteredSensors = Array.AsReadOnly(transitions
            .Where(change => change.WasRegistered)
            .Select(change => change.Sensor).ToArray());
        RemovedSensors = Array.AsReadOnly(transitions
            .Where(change => change.WasRemoved)
            .Select(change => change.Sensor).ToArray());
    }

    public MeshGatewaySpanningTreeRouting Previous { get; }

    public MeshGatewaySpanningTreeRouting Current { get; }

    public long PreviousGraphVersion => Previous.GraphVersion;

    public long CurrentGraphVersion => Current.GraphVersion;

    public bool HasGraphRevisionChanged =>
        PreviousGraphVersion != CurrentGraphVersion;

    /// <summary>
    /// True if selected tree parent/link identities OR selected link costs
    /// changed. A revision caused by an unused edge may leave this false.
    /// </summary>
    public bool HasBackboneChanged { get; }

    /// <summary>
    /// Disconnected islands may merge/split without changing any device's
    /// Disconnected status; surface those changes separately.
    /// </summary>
    public bool HasDisconnectedComponentsChanged { get; }

    /// <summary>Only sensors with a changed route, status or registration.</summary>
    public IReadOnlyList<MeshTreeRouteTransition> RouteTransitions { get; }

    public IReadOnlyList<MeshNodeKey> ReroutedSensors { get; }

    public IReadOnlyList<MeshNodeKey> LostGatewayConnectivitySensors { get; }

    public IReadOnlyList<MeshNodeKey> RegainedGatewayConnectivitySensors { get; }

    public IReadOnlyList<MeshNodeKey> NewlyRegisteredSensors { get; }

    public IReadOnlyList<MeshNodeKey> RemovedSensors { get; }

    private static bool SameBackbone(
        MinimumSpanningTreeResult first,
        MinimumSpanningTreeResult second)
    {
        if (first.SelectedBranches.Count != second.SelectedBranches.Count)
        {
            return false;
        }

        for (int index = 0; index < first.SelectedBranches.Count; index++)
        {
            MeshTreeBranch before = first.SelectedBranches[index];
            MeshTreeBranch after = second.SelectedBranches[index];
            if (before.Parent != after.Parent || before.Child != after.Child ||
                before.Link.Id != after.Link.Id ||
                before.Link.SimulatedEnergyCost != after.Link.SimulatedEnergyCost)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameComponents(
        IReadOnlyList<MeshDisconnectedComponent> first,
        IReadOnlyList<MeshDisconnectedComponent> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Count; index++)
        {
            if (!first[index].Sensors.SequenceEqual(second[index].Sensors))
            {
                return false;
            }
        }

        return true;
    }
}