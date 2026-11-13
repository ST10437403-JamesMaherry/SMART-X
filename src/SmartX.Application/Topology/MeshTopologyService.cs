using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// Read-only HTTP-facing orchestration for P3-07. The existing registry is
/// authoritative for sensor metadata; the existing mesh store is authoritative
/// for configured links and its versioned health projection. This service
/// owns no duplicate topology, route cache, or fabricated device catalogue.
/// </summary>
public sealed class MeshTopologyService
{
    private readonly IMeshTopologyStore _topologyStore;
    private readonly ISensorRepository _sensorRepository;

    public MeshTopologyService(
        IMeshTopologyStore topologyStore,
        ISensorRepository sensorRepository)
    {
        _topologyStore = topologyStore
            ?? throw new ArgumentNullException(nameof(topologyStore));
        _sensorRepository = sensorRepository
            ?? throw new ArgumentNullException(nameof(sensorRepository));
    }

    /// <summary>
    /// Returns configured links and actual registered vertices. Status and
    /// forwarding eligibility come from a SINGLE immutable graph revision.
    /// Registration metadata is read after the graph snapshot so that a
    /// concurrent new registration cannot cause missing profile metadata.
    /// </summary>
    public async Task<MeshTopologyResponse> GetTopologyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<SensorProfile> registered = await _sensorRepository
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<Guid, SensorProfile> byId = registered
            .ToDictionary(profile => profile.Id);

        MeshVertexResponse[] vertices = snapshot.Nodes
            .Select(node => MapVertex(node, byId))
            .ToArray();
        MeshLinkResponse[] links = snapshot.Links
            .Select(MapLink)
            .ToArray();

        return new MeshTopologyResponse(
            snapshot.Version,
            MeshNodeKey.Gateway.ToString(),
            vertices.Length,
            links.Length,
            vertices,
            links);
    }

    /// <summary>
    /// Runs the real P3-05 BFS and disconnected-component classifier against
    /// one authoritative snapshot. No topology is mutated by this read.
    /// </summary>
    public async Task<MeshReachabilityResponse> GetReachabilityAsync(
        CancellationToken cancellationToken = default)
    {
        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        MeshGatewayRouting routing = MeshTraversalService
            .CalculateGatewayRouting(snapshot, cancellationToken);

        return new MeshReachabilityResponse(
            routing.GraphVersion,
            MeshNodeKey.Gateway.ToString(),
            routing.RegisteredSensorCount,
            routing.ReachableSensorCount,
            routing.DisconnectedSensorCount,
            routing.UnavailableSensorCount,
            routing.Reachability.ReachableNodes
                .Select(entry => new MeshHopCountResponse(
                    entry.Node.ToString(), entry.HopCount))
                .ToArray(),
            routing.UnavailableSensors.Select(key => key.ToString()).ToArray(),
            MapComponents(routing.DisconnectedComponents));
    }

    /// <summary>
    /// Runs the real P3-06 priority-heap Prim algorithm on one graph revision.
    /// It minimises selected BACKBONE cost, never each sensor's route cost.
    /// </summary>
    public async Task<MeshMinimumSpanningTreeResponse> GetMinimumSpanningTreeAsync(
        CancellationToken cancellationToken = default)
    {
        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        MeshGatewaySpanningTreeRouting routing = MeshSpanningTreeService
            .CalculateGatewaySpanningTreeRouting(snapshot, cancellationToken);

        return new MeshMinimumSpanningTreeResponse(
            routing.GraphVersion,
            MeshNodeKey.Gateway.ToString(),
            routing.TotalSimulatedEnergyCost,
            routing.RegisteredSensorCount,
            routing.ReachableSensorCount,
            routing.DisconnectedSensorCount,
            routing.UnavailableSensorCount,
            routing.SelectedEdgeCount,
            routing.Tree.ReachedNodes.Select(key => key.ToString()).ToArray(),
            routing.Tree.SelectedBranches.Select(branch =>
                new MeshSelectedTreeEdgeResponse(
                    branch.Link.Id,
                    branch.Parent.ToString(),
                    branch.Child.ToString(),
                    branch.Link.SimulatedEnergyCost)).ToArray(),
            routing.UnavailableSensors.Select(key => key.ToString()).ToArray(),
            MapComponents(routing.DisconnectedComponents));
    }

    /// <summary>
    /// Computes a selected sensor's BFS and Prim routes against EXACTLY ONE
    /// snapshot. It reuses one BFS classification in both projections, rather
    /// than fetching a second revision between BFS and MST. A known sensor
    /// without a gateway path remains distinguishable from an unknown ID.
    /// </summary>
    public async Task<MeshDeviceRouteResponse> GetDeviceRouteAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A mesh route requires a non-empty sensor profile ID.");
        }

        MeshGraphSnapshot snapshot = await _topologyStore
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        MeshNodeKey sensor = MeshNodeKey.ForSensor(sensorProfileId);
        if (!snapshot.Nodes.Any(node => node.Key == sensor))
        {
            throw new SensorNotFoundException(sensorProfileId);
        }

        MeshGatewayRouting bfs = MeshTraversalService
            .CalculateGatewayRouting(snapshot, cancellationToken);
        MinimumSpanningTreeResult tree = MeshSpanningTreeService
            .CalculateGatewayMinimumSpanningTree(snapshot, cancellationToken);
        var mst = new MeshGatewaySpanningTreeRouting(
            tree,
            bfs.RegisteredSensors,
            bfs.UnavailableSensors,
            bfs.DisconnectedComponents);

        bool hasBfs = bfs.TryGetRouteToGateway(sensor, out MeshRoute? hopRoute);
        bool hasMst = mst.TryGetTreeRouteToGateway(sensor, out MeshTreeRoute? treeRoute);
        if (!hasBfs || !hasMst || hopRoute is null || treeRoute is null ||
            hopRoute.GraphVersion != treeRoute.GraphVersion ||
            hopRoute.Status != treeRoute.Status)
        {
            throw new InvalidMeshTopologyException(
                "Gateway BFS and MST projections disagree about this sensor.");
        }

        return new MeshDeviceRouteResponse(
            snapshot.Version,
            sensorProfileId,
            hopRoute.Status.ToString(),
            new MeshHopRouteResponse(
                hopRoute.Status.ToString(),
                hopRoute.HopCount,
                hopRoute.PathToGateway.Select(key => key.ToString()).ToArray()),
            new MeshTreeRouteResponse(
                treeRoute.Status.ToString(),
                treeRoute.HopCount,
                treeRoute.AccumulatedSimulatedEnergyCost,
                treeRoute.PathToGateway.Select(key => key.ToString()).ToArray(),
                treeRoute.SelectedLinksToGateway.Select(link => link.Id).ToArray()));
    }

    private static MeshVertexResponse MapVertex(
        MeshNode node,
        IReadOnlyDictionary<Guid, SensorProfile> registered)
    {
        if (node.IsGateway)
        {
            return new MeshVertexResponse(
                node.Key.ToString(), "Gateway", null, null, null, null,
                null, null, true, null, null);
        }

        if (!registered.TryGetValue(node.Key.SensorProfileId,
                out SensorProfile? profile) || profile is null)
        {
            // This should not happen with the current append-only registry.
            // Never silently fabricate an identity or omit a graph vertex.
            throw new InvalidOperationException(
                "The mesh snapshot contains a sensor missing from the registry.");
        }

        SensorResponse metadata = SensorContractMapper.ToResponse(profile);
        return new MeshVertexResponse(
            node.Key.ToString(),
            "Sensor",
            metadata.Id,
            metadata.Name,
            metadata.DeviceIdentifier,
            metadata.IdentifierKind,
            metadata.Category,
            node.Status?.ToString(),
            node.IsForwardingEligible,
            metadata.TelemetryDataType,
            metadata.DeploymentLocation);
    }

    private static MeshLinkResponse MapLink(MeshLink link) =>
        new(link.Id, link.NodeA.ToString(), link.NodeB.ToString(),
            link.SimulatedEnergyCost);

    private static IReadOnlyList<MeshDisconnectedComponentResponse> MapComponents(
        IEnumerable<MeshDisconnectedComponent> components) =>
        components.Select(component => new MeshDisconnectedComponentResponse(
            component.Sensors.Select(key => key.ToString()).ToArray())).ToArray();
}