using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
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
    private readonly DashboardEventCoordinator _dashboardEvents;

    public TelemetryController(
        SensorTelemetryService telemetryService,
        DashboardEventCoordinator dashboardEvents)
    {
        _telemetryService =
            telemetryService ??
            throw new ArgumentNullException(
                nameof(telemetryService));

        _dashboardEvents =
            dashboardEvents ??
            throw new ArgumentNullException(
                nameof(dashboardEvents));
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

        /*
         * Analysis runs only after the strongly typed packet has been
         * validated and persisted successfully.
         */
        await _dashboardEvents
            .AnalyzeTelemetryAsync(
                sensorProfileId,
                cancellationToken);

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

        /*
         * An entirely empty historical batch contains no connectivity or
         * anomaly evidence. At least one accepted packet is required before
         * operational-state and anomaly analysis is triggered.
         */
        if (result.AcceptedPacketCount > 0)
        {
            await _dashboardEvents
                .AnalyzeTelemetryAsync(
                    sensorProfileId,
                    cancellationToken);
        }

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