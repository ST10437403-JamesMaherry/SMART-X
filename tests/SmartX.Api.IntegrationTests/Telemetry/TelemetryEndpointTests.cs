using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Telemetry;

/// <summary>
/// Exercises telemetry through the complete HTTP -> Application ->
/// Infrastructure pipeline using registered Smart-X sensors.
/// </summary>
public sealed class TelemetryEndpointTests
    : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TelemetryEndpointTests()
    {
        _factory =
            new WebApplicationFactory<Program>();

        _client =
            _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress =
                        new Uri("https://localhost")
                });
    }

    [Fact]
    public async Task Ingest_WhenFloatSensor_ReturnsStronglyTypedReading()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:70",
                "Float");

        var request =
            FloatReading(
                24.75,
                0);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var reading =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>();

        Assert.NotNull(reading);

        Assert.Equal(
            sensor.Id,
            reading.SensorProfileId);

        Assert.Equal(
            "Float",
            reading.TelemetryDataType);

        Assert.Equal(
            24.75,
            reading.FloatValue);

        Assert.Null(
            reading.IntegerValue);

        Assert.Null(
            reading.BooleanValue);
    }

    [Fact]
    public async Task Ingest_WhenIntegerSensor_PreservesZeroValue()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:71",
                "Integer");

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                IntegerReading(
                    0,
                    0));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var reading =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>();

        Assert.NotNull(reading);

        Assert.Equal(
            "Integer",
            reading.TelemetryDataType);

        Assert.Equal(
            0,
            reading.IntegerValue);

        Assert.Null(
            reading.FloatValue);

        Assert.Null(
            reading.BooleanValue);
    }

    [Fact]
    public async Task Ingest_WhenBooleanSensor_PreservesFalseValue()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:72",
                "Boolean");

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                BooleanReading(
                    false,
                    0));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var reading =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>();

        Assert.NotNull(reading);

        Assert.Equal(
            "Boolean",
            reading.TelemetryDataType);

        Assert.False(
            reading.BooleanValue);

        Assert.Null(
            reading.FloatValue);

        Assert.Null(
            reading.IntegerValue);
    }

    [Fact]
    public async Task GetRecent_WhenTelemetryExists_ReturnsNewestLimitedSequence()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:73",
                "Integer");

        await IngestAsync(
            sensor.Id,
            IntegerReading(
                10,
                0));

        await IngestAsync(
            sensor.Id,
            IntegerReading(
                20,
                1));

        await IngestAsync(
            sensor.Id,
            IntegerReading(
                30,
                2));

        var response =
            await _client.GetAsync(
                $"/api/sensors/{sensor.Id}/telemetry?limit=2");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var readings =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse[]>();

        Assert.NotNull(readings);

        Assert.Equal(
            2,
            readings.Length);

        Assert.Equal(
            20,
            readings[0].IntegerValue);

        Assert.Equal(
            30,
            readings[1].IntegerValue);
    }

    [Fact]
    public async Task IngestBatch_WhenUnevenBatches_ProcessesHistoricalTelemetryInOrder()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:74",
                "Float");

        IngestTelemetryRequest?[]?[] batches =
        [
            [
                FloatReading(10.0, 0),
                FloatReading(11.0, 1)
            ],
            [
                FloatReading(20.0, 2)
            ],
            [
                FloatReading(30.0, 3),
                FloatReading(31.0, 4)
            ]
        ];

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry/batches",
                new IngestTelemetryBatchRequest(
                    batches));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var summary =
            await response.Content
                .ReadFromJsonAsync<
                    TelemetryBatchIngestionResponse>();

        Assert.NotNull(summary);

        Assert.Equal(
            3,
            summary.BatchCount);

        Assert.Equal(
            5,
            summary.AcceptedPacketCount);

        var recent =
            await _client.GetFromJsonAsync<
                TelemetryReadingResponse[]>(
                    $"/api/sensors/{sensor.Id}/telemetry?limit=10");

        Assert.NotNull(recent);

        Assert.Equal(
            [10.0, 11.0, 20.0, 30.0, 31.0],
            recent
                .Select(reading => reading.FloatValue!.Value)
                .ToArray());
    }

    [Fact]
    public async Task IngestBatch_WhenBatchEmpty_PreservesValidBatchStructure()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:75",
                "Boolean");

        IngestTelemetryRequest?[]?[] batches =
        [
            [
                BooleanReading(
                    false,
                    0)
            ],
            [],
            [
                BooleanReading(
                    true,
                    1)
            ]
        ];

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry/batches",
                new IngestTelemetryBatchRequest(
                    batches));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var summary =
            await response.Content
                .ReadFromJsonAsync<
                    TelemetryBatchIngestionResponse>();

        Assert.NotNull(summary);

        Assert.Equal(
            3,
            summary.BatchCount);

        Assert.Equal(
            2,
            summary.AcceptedPacketCount);
    }

    [Fact]
    public async Task Ingest_WhenSensorMissing_ReturnsNotFoundProblemDetails()
    {
        var missingId =
            Guid.NewGuid();

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{missingId}/telemetry",
                FloatReading(
                    24.5,
                    0));

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            problem.Status);

        Assert.Equal(
            "Sensor not found",
            problem.Title);
    }

    [Fact]
    public async Task GetRecent_WhenSensorMissing_ReturnsNotFound()
    {
        var response =
            await _client.GetAsync(
                $"/api/sensors/{Guid.NewGuid()}/telemetry");

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);
    }

    [Fact]
    public async Task Ingest_WhenPayloadTypeDoesNotMatchSensor_ReturnsBadRequestProblemDetails()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:76",
                "Float");

        var wrongPayload =
            IntegerReading(
                25,
                0);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                wrongPayload);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            "Invalid telemetry request",
            problem.Title);

        Assert.Contains(
            "FloatValue",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ingest_WhenMultiplePayloadValuesProvided_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:77",
                "Integer");

        var invalid =
            new IngestTelemetryRequest(
                BaseTime,
                25.5,
                25,
                null);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                invalid);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            "Invalid telemetry request",
            problem.Title);
    }

    [Fact]
    public async Task Ingest_WhenTimestampMissing_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:78",
                "Boolean");

        var request =
            new IngestTelemetryRequest(
                null,
                null,
                null,
                true);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                request);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Contains(
            "timestamp",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRecent_WhenLimitInvalid_ReturnsBadRequestProblemDetails()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:79",
                "Float");

        var response =
            await _client.GetAsync(
                $"/api/sensors/{sensor.Id}/telemetry?limit=101");

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            "Invalid telemetry request",
            problem.Title);

        Assert.Contains(
            "between 1 and 100",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ingest_WhenJsonMalformed_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:7A",
                "Float");

        using var content =
            new StringContent(
                """
                {
                  "capturedAtUtc": "2026-09-11T10:00:00Z",
                  "floatValue":
                }
                """,
                Encoding.UTF8,
                "application/json");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                content);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<SensorResponse> RegisterSensorAsync(
        string macAddress,
        string telemetryDataType)
    {
        var request =
            new RegisterSensorRequest(
                $"Test {telemetryDataType} Sensor",
                macAddress,
                "MacAddress",
                new SensorDeploymentLocationRequest(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                "Environmental",
                telemetryDataType);

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        response.EnsureSuccessStatusCode();

        var sensor =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "The sensor registration response was empty.");
    }

    private async Task IngestAsync(
        Guid sensorId,
        IngestTelemetryRequest request)
    {
        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensorId}/telemetry",
                request);

        response.EnsureSuccessStatusCode();
    }

    private static IngestTelemetryRequest FloatReading(
        double value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            value,
            null,
            null);
    }

    private static IngestTelemetryRequest IntegerReading(
        int value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            null,
            value,
            null);
    }

    private static IngestTelemetryRequest BooleanReading(
        bool value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            null,
            null,
            value);
    }

    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            10,
            0,
            0,
            TimeSpan.Zero);
}