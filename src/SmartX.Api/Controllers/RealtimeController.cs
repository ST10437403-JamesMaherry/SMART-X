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
    private readonly ITelemetryWorkBuffer _buffer;
    private readonly ITelemetryProcessingStore _processing;
    private readonly IRealtimeNotifier _notifier;

    public RealtimeController(RealtimeSnapshotService snapshotService,
        ITelemetryWorkBuffer buffer, ITelemetryProcessingStore processing, IRealtimeNotifier notifier)
    {
        _buffer = buffer;
        _processing = processing;
        _notifier = notifier;
        _snapshotService = snapshotService ??
            throw new ArgumentNullException(nameof(snapshotService));
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