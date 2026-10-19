using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Telemetry;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes strongly typed telemetry ingestion, historical batch processing
/// and recent telemetry retrieval for registered Smart-X sensors.
///
/// HTTP concerns remain in this controller while sensor validation, generic
/// packet construction and persistence orchestration remain in Application.
///
/// Successfully persisted telemetry is subsequently passed to the Adaptive
/// Anomaly Spotlight coordinator so dashboard state can react dynamically.
/// </summary>
[ApiController]
[Route("api/sensors/{sensorProfileId:guid}/telemetry")]
public sealed class TelemetryController
    : ControllerBase
{
    private readonly SensorTelemetryService _telemetryService;

    public TelemetryController(
        SensorTelemetryService telemetryService)
    {
        _telemetryService =
            telemetryService ??
            throw new ArgumentNullException(
                nameof(telemetryService));

    }

    /// <summary>
    /// Ingests one telemetry reading for a registered sensor.
    ///
    /// The sensor's configured TelemetryDataType determines which payload
    /// field is accepted and which closed generic TelemetryPacket type is
    /// created.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<TelemetryReadingResponse>(
        StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryReadingResponse>> Ingest(
        Guid sensorProfileId,
        [FromBody] IngestTelemetryRequest request,
        CancellationToken cancellationToken)
    {
        TelemetryReadingResponse result =
            await _telemetryService.IngestAsync(
                sensorProfileId,
                request,
                cancellationToken);

        // The acceptance service has already queued the exact packet and
        // completed the synchronous Part 1 operational effects.
        return StatusCode(
            StatusCodes.Status201Created,
            result);
    }

    /// <summary>
    /// Processes historical telemetry supplied as an ordered jagged sequence
    /// of batches.
    ///
    /// The Application layer converts these batches into the existing
    /// HistoricalTelemetryBuffer&lt;T&gt; custom collection before persistence.
    /// </summary>
    [HttpPost("batches")]
    [ProducesResponseType<TelemetryBatchIngestionResponse>(
        StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<
        TelemetryBatchIngestionResponse>> IngestBatch(
        Guid sensorProfileId,
        [FromBody] IngestTelemetryBatchRequest request,
        CancellationToken cancellationToken)
    {
        TelemetryBatchIngestionResponse result =
            await _telemetryService.IngestBatchAsync(
                sensorProfileId,
                request,
                cancellationToken);

        // Empty batches retain their response shape and create no work.
        return StatusCode(
            StatusCodes.Status201Created,
            result);
    }

    /// <summary>
    /// Returns the latest telemetry readings stored for a sensor.
    ///
    /// Results preserve their original ingestion order. The limit defaults
    /// to 50 and may not exceed 100.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TelemetryReadingResponse>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<
        IReadOnlyList<TelemetryReadingResponse>>> GetRecent(
        Guid sensorProfileId,
        [FromQuery] int limit =
            SensorTelemetryService.DefaultRecentTelemetryLimit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TelemetryReadingResponse> result =
            await _telemetryService.GetRecentAsync(
                sensorProfileId,
                limit,
                cancellationToken);

        return Ok(
            result);
    }
}