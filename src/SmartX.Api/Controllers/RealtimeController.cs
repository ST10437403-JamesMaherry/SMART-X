using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Realtime;
using SmartX.Contracts.Realtime;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes authoritative Part 2 reads over the existing sensor registry.
/// Expected validation and lookup failures use the shared exception handler.
/// </summary>
[ApiController]
[Route("api/realtime")]
public sealed class RealtimeController : ControllerBase
{
    private readonly RealtimeSnapshotService _snapshotService;
    private readonly RealtimeDeviceSearchService _searchService;
    private readonly ITelemetryWorkBuffer _buffer;
    private readonly ITelemetryProcessingStore _processing;
    private readonly IRealtimeNotifier _notifier;

    public RealtimeController(RealtimeSnapshotService snapshotService,
        ITelemetryWorkBuffer buffer, ITelemetryProcessingStore processing,
        IRealtimeNotifier notifier, RealtimeDeviceSearchService searchService)
    {
        _buffer = buffer;
        _processing = processing;
        _notifier = notifier;
        _snapshotService = snapshotService ??
            throw new ArgumentNullException(nameof(snapshotService));
        _searchService = searchService ??
            throw new ArgumentNullException(nameof(searchService));
    }

    /// <summary>
    /// Lists devices, or performs exact lookup when both identifier query
    /// values are provided. Exact lookup returns a one-element collection;
    /// a valid identifier with no match returns 404 ProblemDetails.
    /// </summary>
    [HttpGet("devices")]
    [ProducesResponseType<IReadOnlyList<RealtimeDeviceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RealtimeDeviceResponse>>> GetDevices(
        [FromQuery] string? deviceIdentifier,
        [FromQuery] string? identifierKind,
        CancellationToken cancellationToken)
    {
        // Empty query values may bind to null. Check key presence so an
        // attempted empty lookup cannot silently become an unfiltered list.
        bool lookupRequested =
            Request.Query.ContainsKey(nameof(deviceIdentifier)) ||
            Request.Query.ContainsKey(nameof(identifierKind));

        IReadOnlyList<RealtimeDeviceResponse> devices;

        if (lookupRequested)
        {
            RealtimeDeviceResponse device =
                await _snapshotService.GetByDeviceIdentifierAsync(
                    deviceIdentifier,
                    identifierKind,
                    cancellationToken);

            devices = Array.AsReadOnly(new[] { device });
        }
        else
        {
            devices = await _snapshotService.GetDevicesAsync(cancellationToken);
        }

        return Ok(devices);
    }

    /// <summary>
    /// P2-08: Read-only search over the existing canonical device registry.
    ///
    /// GET /api/realtime/devices/search?text=...&amp;category=...
    ///
    /// Exact identifier searches use the Dictionary lookup. Remaining filters
    /// use the current device/active-condition snapshots, with stable paging.
    /// The existing /devices endpoint keeps its legacy exact-lookup 404 rule.
    ///
    /// Search GETs are intentionally not operator-training events. P2-08
    /// Commit 2 records only explicit submitted user interactions.
    /// </summary>
    [HttpGet("devices/search")]
    [ProducesResponseType<RealtimeDeviceSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RealtimeDeviceSearchResponse>> SearchDevices(
        CancellationToken cancellationToken)
    {
        // Read raw query values so empty/duplicate inputs cannot be silently
        // treated as absent filters by MVC's empty-string normalization.
        // All validation happens before querying the device repository.
        RealtimeDeviceSearchQuery query = new(
            DeviceIdentifier: ReadSearchValue("deviceIdentifier"),
            IdentifierKind: ReadSearchValue("identifierKind"),
            Text: ReadSearchValue("text"),
            Category: ReadSearchValue("category"),
            Status: ReadSearchValue("status"),
            HasActiveCondition: ReadSearchValue("hasActiveCondition"),
            ConditionType: ReadSearchValue("conditionType"),
            Limit: ReadSearchValue("limit"),
            Offset: ReadSearchValue("offset"));

        RealtimeDeviceSearchResponse result =
            await _searchService.SearchAsync(query, cancellationToken);

        return Ok(result);
    }

    private string? ReadSearchValue(string name)
    {
        if (!Request.Query.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new InvalidRealtimeDeviceSearchQueryException(
                $"Search parameter '{name}' must appear exactly once.");
        }

        return values[0] ?? string.Empty;
    }

    /// <summary>
    /// Returns the current device snapshot with its server generation time.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType<RealtimeSnapshotResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RealtimeSnapshotResponse>> GetSnapshot(
        CancellationToken cancellationToken)
    {
        RealtimeSnapshotResponse snapshot =
            await _snapshotService.GetSnapshotAsync(cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>Lifetime metrics and the last 100 actual dispatches.</summary>
    [HttpGet("processing")]
    [ProducesResponseType<TelemetryProcessingResponse>(StatusCodes.Status200OK)]
    public ActionResult<TelemetryProcessingResponse> GetProcessing()
        => Ok(TelemetryProcessingMapper.ToResponse(_processing.GetSnapshot()));

    /// <summary>
    /// SIMULATION CONTROL: pause/resume ordinary dispatch. Ingestion and the
    /// critical lane stay live. A dispatch already executing is not preempted.
    /// </summary>
    [HttpPut("simulation/ordinary-dispatch")]
    [ProducesResponseType<TelemetryProcessingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TelemetryProcessingResponse>> SetOrdinaryDispatch(
        SetOrdinaryDispatchRequest request)
    {
        _buffer.SetOrdinaryDispatchPaused(request.Paused!.Value);
        await _notifier.PublishAsync("SimulationChanged");
        return Ok(TelemetryProcessingMapper.ToResponse(_processing.GetSnapshot()));
    }

}