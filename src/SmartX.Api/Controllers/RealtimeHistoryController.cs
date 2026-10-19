using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Realtime;
using SmartX.Contracts.Realtime;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes chronological operational history for registered Smart-X devices.
///
/// The Application service validates registration authority, query limits,
/// chronological ordering, and maps the authoritative operational events.
///
/// The controller performs one additional HTTP-specific validation:
/// distinguishing an omitted query parameter from an explicitly empty
/// query parameter after ASP.NET Core model binding.
/// </summary>
[ApiController]
[Route("api/realtime/devices/{deviceId:guid}/history")]
public sealed class RealtimeHistoryController : ControllerBase
{
    private readonly RealtimeHistoryService _historyService;

    /// <summary>
    /// Initialises the controller with the existing operational history
    /// application service.
    /// </summary>
    public RealtimeHistoryController(
        RealtimeHistoryService historyService)
    {
        _historyService = historyService
            ?? throw new ArgumentNullException(nameof(historyService));
    }

    /// <summary>
    /// Returns the newest bounded chronological events for a registered
    /// device using the existing SortedDictionary-backed history store.
    ///
    /// Supported query parameters:
    ///
    /// limit:
    ///   1 through 500, defaulting to 100.
    ///
    /// order:
    ///   asc, ascending, desc, or descending.
    ///   Defaults to ascending only when omitted entirely.
    ///
    /// An explicitly empty or whitespace-only order is invalid and
    /// returns HTTP 400 ProblemDetails.
    ///
    /// History is read-only and remains available even when a registered
    /// device is not currently Online.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<RealtimeHistoryResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RealtimeHistoryResponse>> GetHistory(
        Guid deviceId,
        [FromQuery] int? limit,
        [FromQuery] string? order,
        CancellationToken cancellationToken)
    {
        /*
         * ASP.NET Core can bind an explicitly empty query parameter:
         *
         *     ?order=
         *
         * to null when the action parameter is declared as string?.
         *
         * This would make an invalid supplied value indistinguishable
         * from an omitted value, accidentally selecting the default
         * ascending order and returning HTTP 200.
         *
         * Check the original query collection to distinguish these cases:
         *
         * - No "order" parameter: valid, default to ascending.
         * - "order=asc": valid.
         * - "order=desc": valid.
         * - "order=": invalid, HTTP 400.
         * - "order=   ": invalid, HTTP 400.
         */
        bool orderWasProvided =
            HttpContext.Request.Query.ContainsKey("order");

        if (orderWasProvided && string.IsNullOrWhiteSpace(order))
        {
            throw new InvalidRealtimeHistoryQueryException(
                "History order must be asc, ascending, desc, or descending.");
        }

        /*
         * The Application service remains responsible for:
         *
         * - Registered-device validation.
         * - Query-limit validation.
         * - Supported chronological ordering.
         * - Retrieving actual per-device SortedDictionary events.
         * - Mapping typed telemetry and command/Undo metadata.
         *
         * No history data is modified by this endpoint.
         */
        RealtimeHistoryResponse response =
            await _historyService.GetHistoryAsync(
                deviceId,
                limit,
                order,
                cancellationToken);

        return Ok(response);
    }
}