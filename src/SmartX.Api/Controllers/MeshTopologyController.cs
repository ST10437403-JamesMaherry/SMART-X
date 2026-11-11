using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Topology;
using SmartX.Application.Topology;
using SmartX.Contracts.Topology;
using SmartX.Domain.Topology;

namespace SmartX.Api.Controllers;

/// <summary>
/// Immutable topology reads plus explicitly gated simulation-only link edits.
/// Routes and MSTs are always recomputed from the authoritative mesh store;
/// this controller never synthesizes links or controls real radio hardware.
/// </summary>
[ApiController]
[Route("api/topology")]
public sealed class MeshTopologyController : ControllerBase
{
    private readonly MeshTopologyService _topologyService;
    private readonly MeshLinkSimulationService _simulationService;
    private readonly MeshSimulationAccessPolicy _simulationAccess;
    private readonly MeshTopologyRealtimePublisher _topologyChanges;

    public MeshTopologyController(
        MeshTopologyService topologyService,
        MeshLinkSimulationService simulationService,
        MeshSimulationAccessPolicy simulationAccess,
        MeshTopologyRealtimePublisher topologyChanges)
    {
        _topologyService = topologyService
            ?? throw new ArgumentNullException(nameof(topologyService));
        _simulationService = simulationService
            ?? throw new ArgumentNullException(nameof(simulationService));
        _simulationAccess = simulationAccess
            ?? throw new ArgumentNullException(nameof(simulationAccess));
        _topologyChanges = topologyChanges
            ?? throw new ArgumentNullException(nameof(topologyChanges));
    }

    /// <summary>
    /// Gets the versioned, logical gateway and actual registered sensor
    /// vertices plus the explicitly configured weighted undirected links.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MeshTopologyResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeshTopologyResponse>> GetTopology(
        CancellationToken cancellationToken)
    {
        MeshTopologyResponse response = await _topologyService
            .GetTopologyAsync(cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Gets gateway BFS fewest-hop reachability, disconnected components,
    /// and sensors in non-forwarding health states.
    /// </summary>
    [HttpGet("reachability")]
    [ProducesResponseType<MeshReachabilityResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeshReachabilityResponse>> GetReachability(
        CancellationToken cancellationToken)
    {
        MeshReachabilityResponse response = await _topologyService
            .GetReachabilityAsync(cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Gets the gateway-rooted minimum-total-cost spanning backbone and its
    /// selected edges. This is not a per-device shortest-weighted-path API.
    /// </summary>
    [HttpGet("mst")]
    [ProducesResponseType<MeshMinimumSpanningTreeResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeshMinimumSpanningTreeResponse>> GetMst(
        CancellationToken cancellationToken)
    {
        MeshMinimumSpanningTreeResponse response = await _topologyService
            .GetMinimumSpanningTreeAsync(cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Gets two routes for the SAME registered sensor from ONE graph revision:
    /// BFS fewest-hop route and the route along the Prim-selected tree.
    /// Disconnected/unavailable sensors return 200 with explicit statuses;
    /// unknown sensors return 404 and empty GUIDs return 400 ProblemDetails.
    /// </summary>
    [HttpGet("devices/{id:guid}/route")]
    [ProducesResponseType<MeshDeviceRouteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeshDeviceRouteResponse>> GetDeviceRoute(
        Guid id,
        CancellationToken cancellationToken)
    {
        MeshDeviceRouteResponse response = await _topologyService
            .GetDeviceRouteAsync(id, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Creates ONE explicitly configured undirected logical link. The server
    /// issues the link ID; invalid identities, duplicate endpoint pairs,
    /// self-links and invalid simulated costs are rejected by existing
    /// domain/store validation with RFC ProblemDetails.
    /// </summary>
    [HttpPost("links")]
    [ProducesResponseType<MeshLinkResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MeshLinkResponse>> CreateLink(
        [FromBody] CreateMeshLinkRequest request,
        CancellationToken cancellationToken)
    {
        if (!_simulationAccess.IsEnabled)
        {
            return SimulationDisabled();
        }

        MeshLink link = await _simulationService.CreateLinkAsync(
            request.NodeAKey,
            request.NodeBKey,
            request.SimulatedEnergyCost,
            cancellationToken);

        // Invalidate after the authoritative graph write, not before it.
        // A broken hub cannot turn a committed link into an HTTP failure.
        await _topologyChanges.PublishSafelyAsync(
            MeshTopologyChangeReasons.LinkCreated);

        // Location refers to the authoritative collection snapshot. We do
        // not expose a redundant mutable link-by-ID GET endpoint.
        return CreatedAtAction(nameof(GetTopology), MapLink(link));
    }

    /// <summary>
    /// Changes only the weight of an existing configured link. An equal-cost
    /// request is idempotent and does not increment the topology revision.
    /// </summary>
    [HttpPatch("links/{id:guid}/cost")]
    [ProducesResponseType<MeshLinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeshLinkResponse>> UpdateLinkCost(
        Guid id,
        [FromBody] UpdateMeshLinkCostRequest request,
        CancellationToken cancellationToken)
    {
        if (!_simulationAccess.IsEnabled)
        {
            return SimulationDisabled();
        }

        MeshLinkCostUpdateOutcome outcome = await _simulationService
            .UpdateLinkCostTrackedAsync(
                id, request.SimulatedEnergyCost, cancellationToken);

        if (outcome.Link is null)
        {
            return LinkNotFound(id);
        }

        // The graph version does not change on an equal-cost PATCH; avoid
        // broadcasting a no-op refresh under ordinary serial requests.
        if (outcome.Changed)
        {
            await _topologyChanges.PublishSafelyAsync(
                MeshTopologyChangeReasons.LinkCostChanged);
        }

        return Ok(MapLink(outcome.Link));
    }

    /// <summary>
    /// Removes one configured undirected link from both adjacency directions.
    /// Existing BFS and MST GET endpoints then use the revised graph.
    /// </summary>
    [HttpDelete("links/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLink(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!_simulationAccess.IsEnabled)
        {
            return SimulationDisabled();
        }

        bool removed = await _simulationService.RemoveLinkAsync(
            id, cancellationToken);

        if (!removed)
        {
            return LinkNotFound(id);
        }

        await _topologyChanges.PublishSafelyAsync(
            MeshTopologyChangeReasons.LinkRemoved);
        return NoContent();
    }

    private static MeshLinkResponse MapLink(MeshLink link) =>
        new(link.Id, link.NodeA.ToString(), link.NodeB.ToString(),
            link.SimulatedEnergyCost);

    private ObjectResult LinkNotFound(Guid id) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Simulated mesh link not found",
        detail: $"No configured simulated mesh link exists with ID {id:D}.");

    /// <summary>
    /// Defaults to forbidden outside Development/Test, unless an operator
    /// deliberately enables the isolated simulation mode in configuration.
    /// </summary>
    private ObjectResult SimulationDisabled() => Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Mesh simulation is disabled",
        detail: "Link changes are simulation-only and are disabled in this " +
            "environment unless Topology:Simulation:Enabled is explicitly true.");
}