using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Telemetry;

/// <summary>
/// Coordinates strongly typed telemetry ingestion, historical batch
/// processing, recent reads, and AVL-backed historical queries for registered sensors.
///
/// The service resolves the sensor first and uses its configured
/// TelemetryDataType to select the correct closed generic telemetry type.
/// </summary>
public sealed class SensorTelemetryService
{
    public const int DefaultRecentTelemetryLimit = 50;

    public const int MaxRecentTelemetryLimit = 100;

    public const int DefaultRangeTelemetryLimit = 100;

    public const int MaxRangeTelemetryLimit = 1_000;

    private readonly ISensorRepository _sensorRepository;
    private readonly ITelemetryRepository _telemetryRepository;
    private readonly TelemetryAcceptanceService _acceptanceService;

    public SensorTelemetryService(
        ISensorRepository sensorRepository,
        ITelemetryRepository telemetryRepository,
        TelemetryAcceptanceService acceptanceService)
    {
        _sensorRepository =
            sensorRepository ??
            throw new ArgumentNullException(
                nameof(sensorRepository));

        _telemetryRepository =
            telemetryRepository ??
            throw new ArgumentNullException(
                nameof(telemetryRepository));

        _acceptanceService = acceptanceService ??
            throw new ArgumentNullException(nameof(acceptanceService));
    }

    /// <summary>
    /// Validates and stores one strongly typed telemetry reading.
    ///
    /// The registered sensor profile determines which payload type is legal.
    /// A client therefore cannot submit Boolean telemetry to a Float sensor
    /// or otherwise override the sensor's configured data type.
    /// </summary>
    public async Task<TelemetryReadingResponse> IngestAsync(
        Guid sensorProfileId,
        IngestTelemetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);

        if (request is null)
        {
            throw new InvalidTelemetryRequestException(
                "A telemetry request is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await GetSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return sensor.DataType switch
        {
            TelemetryDataType.Float =>
                await IngestFloatAsync(
                    sensor,
                    request,
                    cancellationToken),

            TelemetryDataType.Integer =>
                await IngestIntegerAsync(
                    sensor,
                    request,
                    cancellationToken),

            TelemetryDataType.Boolean =>
                await IngestBooleanAsync(
                    sensor,
                    request,
                    cancellationToken),

            _ =>
                throw new InvalidTelemetryRequestException(
                    $"Sensor '{sensor.Id}' has an unsupported telemetry " +
                    $"data type '{sensor.DataType}'.")
        };
    }

    /// <summary>
    /// Converts raw jagged telemetry batches into the appropriate strongly
    /// typed HistoricalTelemetryBuffer and persists the resulting custom
    /// collection through ITelemetryRepository.
    /// </summary>
    public async Task<TelemetryBatchIngestionResponse> IngestBatchAsync(
        Guid sensorProfileId,
        IngestTelemetryBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);

        if (request is null)
        {
            throw new InvalidTelemetryRequestException(
                "A telemetry batch request is required.");
        }

        if (request.Batches is null)
        {
            throw new InvalidTelemetryRequestException(
                "Telemetry batches cannot be null.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await GetSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return sensor.DataType switch
        {
            TelemetryDataType.Float =>
                await IngestFloatBatchAsync(
                    sensor,
                    request.Batches,
                    cancellationToken),

            TelemetryDataType.Integer =>
                await IngestIntegerBatchAsync(
                    sensor,
                    request.Batches,
                    cancellationToken),

            TelemetryDataType.Boolean =>
                await IngestBooleanBatchAsync(
                    sensor,
                    request.Batches,
                    cancellationToken),

            _ =>
                throw new InvalidTelemetryRequestException(
                    $"Sensor '{sensor.Id}' has an unsupported telemetry " +
                    $"data type '{sensor.DataType}'.")
        };
    }

    /// <summary>
    /// Returns recent telemetry using the payload type configured for the
    /// requested sensor.
    /// </summary>
    public async Task<IReadOnlyList<TelemetryReadingResponse>> GetRecentAsync(
        Guid sensorProfileId,
        int limit = DefaultRecentTelemetryLimit,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);
        ValidateRecentLimit(limit);

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await GetSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return sensor.DataType switch
        {
            TelemetryDataType.Float =>
                await GetRecentFloatAsync(
                    sensor,
                    limit,
                    cancellationToken),

            TelemetryDataType.Integer =>
                await GetRecentIntegerAsync(
                    sensor,
                    limit,
                    cancellationToken),

            TelemetryDataType.Boolean =>
                await GetRecentBooleanAsync(
                    sensor,
                    limit,
                    cancellationToken),

            _ =>
                throw new InvalidTelemetryRequestException(
                    $"Sensor '{sensor.Id}' has an unsupported telemetry " +
                    $"data type '{sensor.DataType}'.")
        };
    }

    /// <summary>
    /// Queries the real AVL index for one registered sensor, retaining the
    /// configured native payload type and a deterministic continuation key.
    /// This does not modify the original ingestion-order history endpoint.
    /// </summary>
    public async Task<TelemetryRangePageResponse> GetRangePageAsync(
        Guid sensorProfileId,
        DateTimeOffset fromInclusive,
        DateTimeOffset toInclusive,
        int limit = DefaultRangeTelemetryLimit,
        DateTimeOffset? afterTimestampUtc = null,
        int? afterTimestampSequence = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(sensorProfileId);

        DateTimeOffset fromUtc = fromInclusive.ToUniversalTime();
        DateTimeOffset toUtc = toInclusive.ToUniversalTime();
        if (fromUtc > toUtc)
        {
            throw new InvalidTelemetryRequestException(
                "fromUtc must not be later than toUtc.");
        }

        if (limit < 1 || limit > MaxRangeTelemetryLimit)
        {
            throw new InvalidTelemetryRequestException(
                $"Range limit must be between 1 and {MaxRangeTelemetryLimit}.");
        }

        if (afterTimestampUtc.HasValue != afterTimestampSequence.HasValue ||
            afterTimestampSequence < 0)
        {
            throw new InvalidTelemetryRequestException(
                "Continuation requires both afterUtc and a nonnegative afterSequence.");
        }

        DateTimeOffset? cursorUtc = afterTimestampUtc?.ToUniversalTime();
        if (cursorUtc.HasValue &&
            (cursorUtc.Value < fromUtc || cursorUtc.Value > toUtc))
        {
            throw new InvalidTelemetryRequestException(
                "Continuation must fall inside the requested timestamp range.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sensor = await GetSensorAsync(sensorProfileId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return sensor.DataType switch
        {
            TelemetryDataType.Float =>
                await MapRangePageAsync<double>(sensor, fromUtc, toUtc,
                    limit, cursorUtc, afterTimestampSequence,
                    TelemetryContractMapper.ToResponse, cancellationToken),

            TelemetryDataType.Integer =>
                await MapRangePageAsync<int>(sensor, fromUtc, toUtc,
                    limit, cursorUtc, afterTimestampSequence,
                    TelemetryContractMapper.ToResponse, cancellationToken),

            TelemetryDataType.Boolean =>
                await MapRangePageAsync<bool>(sensor, fromUtc, toUtc,
                    limit, cursorUtc, afterTimestampSequence,
                    TelemetryContractMapper.ToResponse, cancellationToken),

            _ => throw new InvalidTelemetryRequestException(
                $"Sensor '{sensor.Id}' has an unsupported telemetry data type.")
        };
    }

    private async Task<TelemetryRangePageResponse> MapRangePageAsync<T>(
        SensorProfile sensor,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        DateTimeOffset? cursorUtc,
        int? cursorSequence,
        Func<Guid, TelemetryPacket<T>, TelemetryReadingResponse> mapPacket,
        CancellationToken cancellationToken)
        where T : struct
    {
        AvlRangePage<T> page;
        try
        {
            page = await _telemetryRepository.GetRangePageAsync<T>(
                sensor.Id, fromUtc, toUtc, limit, cursorUtc, cursorSequence,
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            // Invalid cursor positions are client errors, never HTTP 500.
            throw new InvalidTelemetryRequestException(
                "The historical telemetry continuation is invalid.", exception);
        }

        TelemetryReadingResponse[] readings = page.Entries
            .Select(entry => mapPacket(sensor.Id, entry.Packet))
            .ToArray();

        AvlRangeEntry<T>? lastEntry = page.Entries.LastOrDefault();
        TelemetryRangeCursor? nextCursor = page.HasMore && lastEntry is not null
            ? new TelemetryRangeCursor(
                lastEntry.Packet.CapturedAtUtc, lastEntry.TimestampSequence)
            : null;

        return new TelemetryRangePageResponse(
            sensor.Id, sensor.DataType.ToString(), fromUtc, toUtc, limit,
            Array.AsReadOnly(readings), page.HasMore, nextCursor);
    }

    private async Task<TelemetryReadingResponse> IngestFloatAsync(
        SensorProfile sensor,
        IngestTelemetryRequest request,
        CancellationToken cancellationToken)
    {
        var packet =
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                GetFloatPayload(request),
                GetCaptureTimestamp(request));

        await _acceptanceService.AcceptAsync(
            sensor,
            packet,
            cancellationToken);

        return TelemetryContractMapper.ToResponse(
            sensor.Id,
            packet);
    }

    private async Task<TelemetryReadingResponse> IngestIntegerAsync(
        SensorProfile sensor,
        IngestTelemetryRequest request,
        CancellationToken cancellationToken)
    {
        var packet =
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                GetIntegerPayload(request),
                GetCaptureTimestamp(request));

        await _acceptanceService.AcceptAsync(
            sensor,
            packet,
            cancellationToken);

        return TelemetryContractMapper.ToResponse(
            sensor.Id,
            packet);
    }

    private async Task<TelemetryReadingResponse> IngestBooleanAsync(
        SensorProfile sensor,
        IngestTelemetryRequest request,
        CancellationToken cancellationToken)
    {
        var packet =
            new TelemetryPacket<bool>(
                sensor.DeviceIdentifier,
                GetBooleanPayload(request),
                GetCaptureTimestamp(request));

        await _acceptanceService.AcceptAsync(
            sensor,
            packet,
            cancellationToken);

        return TelemetryContractMapper.ToResponse(
            sensor.Id,
            packet);
    }

    private async Task<TelemetryBatchIngestionResponse>
        IngestFloatBatchAsync(
            SensorProfile sensor,
            IngestTelemetryRequest?[]?[] batches,
            CancellationToken cancellationToken)
    {
        var rawBatches =
            MapBatches<double>(
                batches,
                request =>
                    new NumericTelemetryPacket<double>(
                        sensor.DeviceIdentifier,
                        GetFloatPayload(request),
                        GetCaptureTimestamp(request)));

        var history =
            HistoricalTelemetryBuffer<double>
                .FromJaggedBatches(rawBatches);

        await _acceptanceService.AcceptBatchAsync(
            sensor,
            history,
            cancellationToken);

        return CreateBatchResponse(
            sensor,
            history.BatchCount,
            history.Count);
    }

    private async Task<TelemetryBatchIngestionResponse>
        IngestIntegerBatchAsync(
            SensorProfile sensor,
            IngestTelemetryRequest?[]?[] batches,
            CancellationToken cancellationToken)
    {
        var rawBatches =
            MapBatches<int>(
                batches,
                request =>
                    new NumericTelemetryPacket<int>(
                        sensor.DeviceIdentifier,
                        GetIntegerPayload(request),
                        GetCaptureTimestamp(request)));

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        await _acceptanceService.AcceptBatchAsync(
            sensor,
            history,
            cancellationToken);

        return CreateBatchResponse(
            sensor,
            history.BatchCount,
            history.Count);
    }

    private async Task<TelemetryBatchIngestionResponse>
        IngestBooleanBatchAsync(
            SensorProfile sensor,
            IngestTelemetryRequest?[]?[] batches,
            CancellationToken cancellationToken)
    {
        var rawBatches =
            MapBatches<bool>(
                batches,
                request =>
                    new TelemetryPacket<bool>(
                        sensor.DeviceIdentifier,
                        GetBooleanPayload(request),
                        GetCaptureTimestamp(request)));

        var history =
            HistoricalTelemetryBuffer<bool>
                .FromJaggedBatches(rawBatches);

        await _acceptanceService.AcceptBatchAsync(
            sensor,
            history,
            cancellationToken);

        return CreateBatchResponse(
            sensor,
            history.BatchCount,
            history.Count);
    }

    private async Task<IReadOnlyList<TelemetryReadingResponse>>
        GetRecentFloatAsync(
            SensorProfile sensor,
            int limit,
            CancellationToken cancellationToken)
    {
        var packets =
            await _telemetryRepository.GetRecentAsync<double>(
                sensor.Id,
                limit,
                cancellationToken);

        return packets
            .Select(
                packet =>
                    TelemetryContractMapper.ToResponse(
                        sensor.Id,
                        packet))
            .ToArray();
    }

    private async Task<IReadOnlyList<TelemetryReadingResponse>>
        GetRecentIntegerAsync(
            SensorProfile sensor,
            int limit,
            CancellationToken cancellationToken)
    {
        var packets =
            await _telemetryRepository.GetRecentAsync<int>(
                sensor.Id,
                limit,
                cancellationToken);

        return packets
            .Select(
                packet =>
                    TelemetryContractMapper.ToResponse(
                        sensor.Id,
                        packet))
            .ToArray();
    }

    private async Task<IReadOnlyList<TelemetryReadingResponse>>
        GetRecentBooleanAsync(
            SensorProfile sensor,
            int limit,
            CancellationToken cancellationToken)
    {
        var packets =
            await _telemetryRepository.GetRecentAsync<bool>(
                sensor.Id,
                limit,
                cancellationToken);

        return packets
            .Select(
                packet =>
                    TelemetryContractMapper.ToResponse(
                        sensor.Id,
                        packet))
            .ToArray();
    }

    private async Task<SensorProfile> GetSensorAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
    {
        var sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        return sensor;
    }

    private static TelemetryPacket<T>[][] MapBatches<T>(
        IngestTelemetryRequest?[]?[] batches,
        Func<IngestTelemetryRequest, TelemetryPacket<T>> packetFactory)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(packetFactory);

        var rawBatches =
            new TelemetryPacket<T>[batches.Length][];

        for (var batchIndex = 0;
             batchIndex < batches.Length;
             batchIndex++)
        {
            var batch =
                batches[batchIndex];

            if (batch is null)
            {
                throw new InvalidTelemetryRequestException(
                    $"Telemetry batch {batchIndex} cannot be null.");
            }

            var packets =
                new TelemetryPacket<T>[batch.Length];

            for (var sequenceIndex = 0;
                 sequenceIndex < batch.Length;
                 sequenceIndex++)
            {
                var request =
                    batch[sequenceIndex];

                if (request is null)
                {
                    throw new InvalidTelemetryRequestException(
                        $"Telemetry packet at batch {batchIndex}, " +
                        $"sequence {sequenceIndex} cannot be null.");
                }

                packets[sequenceIndex] =
                    packetFactory(request);
            }

            rawBatches[batchIndex] =
                packets;
        }

        return rawBatches;
    }

    private static double GetFloatPayload(
        IngestTelemetryRequest request)
    {
        if (!request.FloatValue.HasValue ||
            request.IntegerValue.HasValue ||
            request.BooleanValue.HasValue)
        {
            throw new InvalidTelemetryRequestException(
                "A Float sensor requires exactly one FloatValue payload.");
        }

        return request.FloatValue.Value;
    }

    private static int GetIntegerPayload(
        IngestTelemetryRequest request)
    {
        if (!request.IntegerValue.HasValue ||
            request.FloatValue.HasValue ||
            request.BooleanValue.HasValue)
        {
            throw new InvalidTelemetryRequestException(
                "An Integer sensor requires exactly one IntegerValue payload.");
        }

        return request.IntegerValue.Value;
    }

    private static bool GetBooleanPayload(
        IngestTelemetryRequest request)
    {
        if (!request.BooleanValue.HasValue ||
            request.FloatValue.HasValue ||
            request.IntegerValue.HasValue)
        {
            throw new InvalidTelemetryRequestException(
                "A Boolean sensor requires exactly one BooleanValue payload.");
        }

        return request.BooleanValue.Value;
    }

    private static DateTimeOffset GetCaptureTimestamp(
        IngestTelemetryRequest request)
    {
        if (!request.CapturedAtUtc.HasValue ||
            request.CapturedAtUtc.Value == default)
        {
            throw new InvalidTelemetryRequestException(
                "A valid telemetry capture timestamp is required.");
        }

        return request.CapturedAtUtc.Value;
    }

    private static TelemetryBatchIngestionResponse CreateBatchResponse(
        SensorProfile sensor,
        int batchCount,
        int acceptedPacketCount)
    {
        return new TelemetryBatchIngestionResponse(
            sensor.Id,
            sensor.DataType.ToString(),
            batchCount,
            acceptedPacketCount);
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidTelemetryRequestException(
                "A valid sensor profile ID is required.");
        }
    }

    private static void ValidateRecentLimit(
        int limit)
    {
        if (limit <= 0 ||
            limit > MaxRecentTelemetryLimit)
        {
            throw new InvalidTelemetryRequestException(
                $"The recent telemetry limit must be between 1 and " +
                $"{MaxRecentTelemetryLimit}.");
        }
    }
}