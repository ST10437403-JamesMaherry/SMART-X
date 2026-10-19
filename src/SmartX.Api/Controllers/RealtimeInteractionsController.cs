using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Interactions;
using SmartX.Application.Realtime;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Interactions;

namespace SmartX.Api.Controllers;

/// <summary>
/// Explicit, bounded operator evidence for P2-09. Only successful submitted
/// POSTs notify listeners; passive GETs and invalid submissions do not.
/// </summary>
[ApiController]
[Route("api/realtime/interactions")]
public sealed class RealtimeInteractionsController : ControllerBase
{
    private readonly OperatorInteractionService _service;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<RealtimeInteractionsController> _logger;

    public RealtimeInteractionsController(
        OperatorInteractionService service,
        IRealtimeNotifier notifier,
        ILogger<RealtimeInteractionsController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records a submitted search or deliberate filter change, never a GET
    /// refresh. The application service re-executes the query for validation.
    /// </summary>
    [HttpPost("search")]
    [ProducesResponseType<OperatorInteractionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatorInteractionResponse>> RecordSearch(
        [FromBody] RecordDeviceSearchRequest request, CancellationToken cancellationToken)
    {
        OperatorInteraction entry = await _service.RecordSearchAsync(
            request, cancellationToken);
        await NotifyAfterRecordingAsync(entry);

        return StatusCode(StatusCodes.Status201Created,
            OperatorInteractionService.ToResponse(entry));
    }

    /// <summary>Records one explicitly requested, server-validated device/alert view.</summary>
    [HttpPost("view")]
    [ProducesResponseType<OperatorInteractionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatorInteractionResponse>> RecordView(
        [FromBody] RecordOperatorViewRequest request, CancellationToken cancellationToken)
    {
        OperatorInteraction entry = await _service.RecordViewAsync(
            request, cancellationToken);
        await NotifyAfterRecordingAsync(entry);

        return StatusCode(StatusCodes.Status201Created,
            OperatorInteractionService.ToResponse(entry));
    }

    /// <summary>Read-only history: inspecting evidence never creates new evidence.</summary>
    [HttpGet]
    [ProducesResponseType<OperatorInteractionHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<OperatorInteractionHistoryResponse> History([FromQuery] int limit = 50)
        => Ok(_service.GetHistory(limit));

    /// <summary>
    /// Reuses the existing P2-07 RealtimeChanged SignalR message so P2-10 can
    /// refresh authoritative recommendations after deliberate operator input.
    /// The notifier runs AFTER persistence and failure cannot roll back an
    /// already saved event or misrepresent it as an unsuccessful HTTP request.
    /// </summary>
    private async Task NotifyAfterRecordingAsync(OperatorInteraction entry)
    {
        try
        {
            // Do not carry a disconnected caller's cancellation token across
            // the successful persistence boundary.
            await _notifier.PublishAsync(
                "OperatorInteractionRecorded", entry.DeviceId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Operator event {Sequence} was recorded, but real-time invalidation failed.",
                entry.Sequence);
        }
    }
}