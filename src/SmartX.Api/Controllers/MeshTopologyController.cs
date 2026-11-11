using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Topology;
using SmartX.Contracts.Topology;

namespace SmartX.Api.Controllers;

/// <summary>
/// Read-only mesh snapshots and actual BFS/Prim routing results. The current
/// endpoints do not add, remove, or infer links; validated simulation writes
/// are introduced separately in P3-07 Commit 2.
/// </summary>
[ApiController]
[Route("api/topology")]
public sealed class MeshTopologyController : ControllerBase
{
    private readonly MeshTopologyService _topologyService;

    public MeshTopologyController(MeshTopologyService topologyService)
    {
        _topologyService = topologyService
            ?? throw new ArgumentNullException(nameof(topologyService));
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
}