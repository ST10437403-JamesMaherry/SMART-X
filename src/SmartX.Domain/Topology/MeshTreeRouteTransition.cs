namespace SmartX.Domain.Topology;

/// <summary>
/// One meaningful change to a sensor's gateway route between two immutable
/// mesh revisions. A null endpoint means the sensor was not registered in
/// that revision; it must NOT be described as an Offline sensor.
///
/// GraphVersion is deliberately ignored by SameRoute: a new graph revision
/// may leave a sensor's selected route unchanged (for example, an unused
/// link changes cost). Consumers still receive the new revision via the
/// surrounding MeshSpanningTreeRefresh.Current result.
/// </summary>
public sealed class MeshTreeRouteTransition
{
    public MeshTreeRouteTransition(
        MeshNodeKey sensor,
        MeshTreeRoute? previousRoute,
        MeshTreeRoute? currentRoute)
    {
        if (!sensor.IsValid || sensor.IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "A route transition requires a valid sensor identity.");
        }

        if ((previousRoute is not null && previousRoute.Sensor != sensor) ||
            (currentRoute is not null && currentRoute.Sensor != sensor))
        {
            throw new InvalidMeshTopologyException(
                "Both route transition endpoints must belong to the same sensor.");
        }

        if (SameRoute(previousRoute, currentRoute))
        {
            throw new InvalidMeshTopologyException(
                "A route transition must describe an observable route change.");
        }

        Sensor = sensor;
        PreviousRoute = previousRoute;
        CurrentRoute = currentRoute;
    }

    public MeshNodeKey Sensor { get; }

    /// <summary>Null only when this sensor was not previously registered.</summary>
    public MeshTreeRoute? PreviousRoute { get; }

    /// <summary>Null only when this sensor is no longer registered.</summary>
    public MeshTreeRoute? CurrentRoute { get; }

    public bool WasRegistered => PreviousRoute is null;

    public bool WasRemoved => CurrentRoute is null;

    /// <summary>A formerly reachable sensor now lacks a gateway tree path.</summary>
    public bool LostGatewayConnectivity =>
        PreviousRoute?.Status == MeshRouteStatus.Reachable &&
        CurrentRoute?.Status != MeshRouteStatus.Reachable;

    /// <summary>A previously registered, unreachable sensor became reachable.</summary>
    public bool RegainedGatewayConnectivity =>
        PreviousRoute is not null &&
        PreviousRoute.Status != MeshRouteStatus.Reachable &&
        CurrentRoute?.Status == MeshRouteStatus.Reachable;

    /// <summary>
    /// Both routes are reachable but the selected tree path or link identities
    /// changed. An unchanged path with a modified weight is not a reroute.
    /// </summary>
    public bool WasRerouted =>
        PreviousRoute is { Status: MeshRouteStatus.Reachable } before &&
        CurrentRoute is { Status: MeshRouteStatus.Reachable } after &&
        (!before.PathToGateway.SequenceEqual(after.PathToGateway) ||
         !before.SelectedLinksToGateway.Select(link => link.Id)
             .SequenceEqual(after.SelectedLinksToGateway.Select(link => link.Id)));

    /// <summary>
    /// A reachable route's simulated accumulated cost changed, whether due
    /// to a new tree path or to a cost update on an existing selected link.
    /// </summary>
    public bool HasCostChange =>
        PreviousRoute is { Status: MeshRouteStatus.Reachable } before &&
        CurrentRoute is { Status: MeshRouteStatus.Reachable } after &&
        before.AccumulatedSimulatedEnergyCost !=
        after.AccumulatedSimulatedEnergyCost;

    /// <summary>
    /// Semantic comparison: route status, ordered vertices, selected link IDs
    /// and their costs. Revision number alone does not imply route changes.
    /// </summary>
    internal static bool SameRoute(MeshTreeRoute? first, MeshTreeRoute? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        if (first.Sensor != second.Sensor || first.Status != second.Status ||
            !first.PathToGateway.SequenceEqual(second.PathToGateway) ||
            first.SelectedLinksToGateway.Count != second.SelectedLinksToGateway.Count)
        {
            return false;
        }

        for (int index = 0; index < first.SelectedLinksToGateway.Count; index++)
        {
            MeshLink before = first.SelectedLinksToGateway[index];
            MeshLink after = second.SelectedLinksToGateway[index];
            if (before.Id != after.Id ||
                before.SimulatedEnergyCost != after.SimulatedEnergyCost)
            {
                return false;
            }
        }

        return true;
    }
}