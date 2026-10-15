using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Interactions;
using SmartX.Contracts.Realtime;

namespace SmartX.Api.Controllers;

/// <summary>Explicit, bounded, process-local operator evidence for P2-09.</summary>
[ApiController]
[Route("api/realtime/interactions")]
public sealed class RealtimeInteractionsController(OperatorInteractionService service) : ControllerBase
{
    /// <summary>Records a submitted search or deliberate filter change, never GET refresh.</summary>
    [HttpPost("search")]
    [ProducesResponseType<OperatorInteractionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatorInteractionResponse>> RecordSearch(
        [FromBody] RecordDeviceSearchRequest request, CancellationToken cancellationToken)
    {
        var entry = await service.RecordSearchAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, OperatorInteractionService.ToResponse(entry));
    }

    /// <summary>Explicitly records one device/active-alert view.</summary>
    [HttpPost("view")]
    [ProducesResponseType<OperatorInteractionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatorInteractionResponse>> RecordView(
        [FromBody] RecordOperatorViewRequest request, CancellationToken cancellationToken)
    {
        var entry = await service.RecordViewAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, OperatorInteractionService.ToResponse(entry));
    }

    /// <summary>Read-only inspection; querying evidence must not create evidence.</summary>
    [HttpGet]
    [ProducesResponseType<OperatorInteractionHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<OperatorInteractionHistoryResponse> History([FromQuery] int limit = 50)
        => Ok(service.GetHistory(limit));
}