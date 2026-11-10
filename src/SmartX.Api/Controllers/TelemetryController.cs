using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Telemetry;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes strongly typed telemetry ingestion, historical batch processing
/// recent telemetry retrieval and AVL-backed historical range queries for registered Smart-X sensors.
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
    // RFC 3339-style timestamps must declare Z or an explicit +/-HH:mm
    // offset. DateTimeOffset.TryParse alone would accept unspecified local time.
    private static readonly Regex ExplicitOffsetTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Returns an AVL-backed historical telemetry page for one registered
    /// sensor. Equal-timestamp readings are continued with the composite
    /// (afterUtc, afterSequence) key; neither value is an array offset into
    /// the entire telemetry series.
    /// </summary>
    /// <remarks>
    /// Required: fromUtc and toUtc (ISO 8601 with Z or explicit offset).
    /// Optional: limit (1..1000, default 100), afterUtc and afterSequence
    /// (both required for continuation). Never changes GET /telemetry.
    /// </remarks>
    [HttpGet("range")]
    [ProducesResponseType<TelemetryRangePageResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryRangePageResponse>> GetRange(
        Guid sensorProfileId,
        [FromQuery] string? fromUtc,
        [FromQuery] string? toUtc,
        [FromQuery] int? limit,
        [FromQuery] string? afterUtc,
        [FromQuery] int? afterSequence,
        CancellationToken cancellationToken)
    {
        DateTimeOffset from = ParseRequiredTimestamp(fromUtc, "fromUtc");
        DateTimeOffset to = ParseRequiredTimestamp(toUtc, "toUtc");

        // ASP.NET can bind '?limit=' to null. Distinguish that from
        // omitting the key entirely, which legitimately selects the default.
        bool limitSupplied = Request.Query.ContainsKey("limit");
        if (limitSupplied && !limit.HasValue)
        {
            throw new InvalidTelemetryRequestException(
                "limit must be an integer between 1 and 1000.");
        }

        bool afterTimeSupplied = Request.Query.ContainsKey("afterUtc");
        bool afterSequenceSupplied = Request.Query.ContainsKey("afterSequence");
        if (afterTimeSupplied != afterSequenceSupplied ||
            (afterSequenceSupplied && !afterSequence.HasValue))
        {
            throw new InvalidTelemetryRequestException(
                "Continuation requires both afterUtc and afterSequence.");
        }

        DateTimeOffset? parsedAfter = afterTimeSupplied
            ? ParseRequiredTimestamp(afterUtc, "afterUtc")
            : null;

        TelemetryRangePageResponse result =
            await _telemetryService.GetRangePageAsync(
                sensorProfileId, from, to,
                limit ?? SensorTelemetryService.DefaultRangeTelemetryLimit,
                parsedAfter, afterSequence, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Requires explicit timezone information to prevent ambiguity when the
    /// API server and browser run in different time zones.
    /// </summary>
    private static DateTimeOffset ParseRequiredTimestamp(
        string? timestamp,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(timestamp) ||
            !ExplicitOffsetTimestamp.IsMatch(timestamp) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset parsed))
        {
            throw new InvalidTelemetryRequestException(
                $"{parameterName} must be an ISO 8601 timestamp with Z or an explicit timezone offset.");
        }

        return parsed.ToUniversalTime();
    }

}