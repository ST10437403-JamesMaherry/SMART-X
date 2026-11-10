using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Telemetry;

/// <summary>
/// P3-03 end-to-end proof that the historical API reads the actual accepted
/// telemetry series through its AVL secondary index. All writes and reads use
/// real HTTP endpoints and the normal application/infrastructure services.
///
/// Each test owns a separate application factory and process-local stores.
/// No mocked repository, fabricated controller result or timing assertion is
/// used to establish correctness.
/// </summary>
public sealed class TelemetryRangeEndpointTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public TelemetryRangeEndpointTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task Range_LargeJaggedBatch_MatchesIndependentOrderingAndPreservesLegacyRecentHistory()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "A1");

        // A relatively prime multiplier scatters timestamps through the input.
        // Values are unique arrival IDs, allowing independent proof of stable
        // insertion order within each equal-timestamp AVL bucket.
        IngestTelemetryRequest[] source = Enumerable.Range(0, 241)
            .Select(index => IntegerReading(index, BaseTime.AddSeconds((index * 37) % 43)))
            .ToArray();

        using HttpResponseMessage ingestion = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest(
                [source[..67], [], source[67..172], source[172..]]));

        Assert.Equal(HttpStatusCode.Created, ingestion.StatusCode);
        TelemetryBatchIngestionResponse? summary =
            await ingestion.Content.ReadFromJsonAsync<TelemetryBatchIngestionResponse>();
        Assert.NotNull(summary);
        Assert.Equal(4, summary.BatchCount);
        Assert.Equal(source.Length, summary.AcceptedPacketCount);

        // The oracle sorts the independent input array; it never reads a
        // repository or invokes the AVL implementation under test.
        int[] expected = Enumerable.Range(0, source.Length)
            .OrderBy(index => source[index].CapturedAtUtc!.Value.ToUniversalTime())
            .ThenBy(index => index)
            .ToArray();

        var actual = new List<int>();
        DateTimeOffset? afterUtc = null;
        int? afterSequence = null;
        int pages = 0;

        do
        {
            TelemetryRangePageResponse page = await GetRangeAsync(
                sensor.Id, BaseTime, BaseTime.AddSeconds(42), 17,
                afterUtc, afterSequence);

            Assert.Equal(sensor.Id, page.SensorProfileId);
            Assert.Equal("Integer", page.TelemetryDataType);
            Assert.Equal(17, page.Limit);
            Assert.InRange(page.Readings.Count, 1, 17);
            Assert.All(page.Readings, reading =>
            {
                Assert.Equal(sensor.Id, reading.SensorProfileId);
                Assert.Null(reading.FloatValue);
                Assert.Null(reading.BooleanValue);
                Assert.True(reading.IntegerValue.HasValue);
            });

            actual.AddRange(page.Readings.Select(reading => reading.IntegerValue!.Value));
            pages++;
            Assert.True(pages <= 16, "A bounded page traversal must make progress.");

            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            Assert.NotNull(page.NextCursor);
            Assert.Equal(page.Readings[^1].CapturedAtUtc, page.NextCursor.CapturedAtUtc);
            Assert.True(page.NextCursor.Sequence >= 0);
            afterUtc = page.NextCursor.CapturedAtUtc;
            afterSequence = page.NextCursor.Sequence;
        }
        while (true);

        Assert.Equal(expected, actual.ToArray());
        Assert.Equal(source.Length, actual.Distinct().Count());

        // The existing Part 1 endpoint must retain ingestion order, not the
        // chronology used by the new /range endpoint.
        TelemetryReadingResponse[]? recent = await _client.GetFromJsonAsync<
            TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.NotNull(recent);
        Assert.Equal(Enumerable.Range(141, 100).ToArray(),
            recent.Select(reading => reading.IntegerValue!.Value).ToArray());

        // A small interior interval proves that the API filters by capture
        // timestamp rather than returning the latest accepted values.
        TelemetryRangePageResponse narrow = await GetRangeAsync(
            sensor.Id, BaseTime.AddSeconds(20), BaseTime.AddSeconds(22), 100);
        int[] expectedNarrow = expected
            .Where(index => source[index].CapturedAtUtc >= BaseTime.AddSeconds(20) &&
                source[index].CapturedAtUtc <= BaseTime.AddSeconds(22))
            .ToArray();
        Assert.Equal(expectedNarrow,
            narrow.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
        Assert.False(narrow.HasMore);
    }

    [Fact]
    public async Task Range_ThousandsOfAcceptedPackets_FindNarrowWindowAndRespectMaximumPageSize()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "AD");
        const int count = 3_072;

        // 67 and 3072 are coprime. Each packet has a distinct timestamp,
        // but admission order differs substantially from capture order.
        // This exercises a large real AVL, not a sorted test-only collection.
        IngestTelemetryRequest[] input = Enumerable.Range(0, count)
            .Select(index => IntegerReading(index,
                BaseTime.AddSeconds((index * 67) % count)))
            .ToArray();

        using HttpResponseMessage accepted = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest(
                [input[..777], [], input[777..1977], input[1977..]]));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        TelemetryBatchIngestionResponse? receipt = await accepted.Content
            .ReadFromJsonAsync<TelemetryBatchIngestionResponse>();
        Assert.NotNull(receipt);
        Assert.Equal(count, receipt.AcceptedPacketCount);
        Assert.Equal(4, receipt.BatchCount);

        // The server must enforce the 1000-item contract, even though this
        // particular series contains more than three times that amount.
        TelemetryRangePageResponse first = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime.AddSeconds(count - 1), 1_000);
        Assert.Equal(1_000, first.Readings.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        // Nine requested seconds out of 3072 is a true narrow-range lookup.
        // Compute expected values from the original submission array only.
        const int lowerSecond = 800;
        const int upperSecond = 808;
        int[] expected = Enumerable.Range(0, count)
            .Where(index => ((index * 67) % count) >= lowerSecond &&
                ((index * 67) % count) <= upperSecond)
            .OrderBy(index => (index * 67) % count)
            .ToArray();

        TelemetryRangePageResponse narrow = await GetRangeAsync(
            sensor.Id, BaseTime.AddSeconds(lowerSecond),
            BaseTime.AddSeconds(upperSecond), 1_000);
        Assert.Equal(9, expected.Length);
        Assert.Equal(expected,
            narrow.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
        Assert.False(narrow.HasMore);
        Assert.Null(narrow.NextCursor);
    }

    [Fact]
    public async Task Range_EntireDuplicateTimestampBucket_PagesWithoutGapsOrRepetitions()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "A2");
        IngestTelemetryRequest[] duplicates = Enumerable.Range(0, 53)
            .Select(index => IntegerReading(index, BaseTime))
            .ToArray();

        await IngestBatchAsync(sensor.Id, [duplicates[..21], duplicates[21..]]);

        var observed = new List<int>();
        DateTimeOffset? afterUtc = null;
        int? afterSequence = null;
        int pageCount = 0;

        do
        {
            TelemetryRangePageResponse page = await GetRangeAsync(
                sensor.Id, BaseTime, BaseTime, 7, afterUtc, afterSequence);
            observed.AddRange(page.Readings.Select(reading => reading.IntegerValue!.Value));
            pageCount++;

            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            Assert.NotNull(page.NextCursor);
            Assert.Equal(BaseTime, page.NextCursor.CapturedAtUtc);
            Assert.Equal(observed.Count - 1, page.NextCursor.Sequence);
            afterUtc = page.NextCursor.CapturedAtUtc;
            afterSequence = page.NextCursor.Sequence;
        }
        while (true);

        Assert.Equal(8, pageCount);
        Assert.Equal(Enumerable.Range(0, 53).ToArray(), observed.ToArray());
    }

    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task Range_AllNativeTypes_PreserveZeroFalseAndTypedNullFields(string type)
    {
        SensorResponse sensor = await RegisterAsync(type, "A3");
        IngestTelemetryRequest reading = type switch
        {
            "Float" => new IngestTelemetryRequest(BaseTime, 0.0, null, null),
            "Integer" => new IngestTelemetryRequest(BaseTime, null, 0, null),
            _ => new IngestTelemetryRequest(BaseTime, null, null, false)
        };

        await IngestAsync(sensor.Id, reading);
        TelemetryRangePageResponse page = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime, 10);
        TelemetryReadingResponse item = Assert.Single(page.Readings);

        Assert.Equal(type, page.TelemetryDataType);
        Assert.Equal(type, item.TelemetryDataType);
        Assert.Equal(sensor.Id, item.SensorProfileId);
        Assert.Equal(reading.FloatValue, item.FloatValue);
        Assert.Equal(reading.IntegerValue, item.IntegerValue);
        Assert.Equal(reading.BooleanValue, item.BooleanValue);
        Assert.Equal(BaseTime, item.CapturedAtUtc);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Range_RegisteredSensorWithNoReadings_ReturnsEmptySuccessfulDefaultPage()
    {
        SensorResponse sensor = await RegisterAsync("Boolean", "A4");
        TelemetryRangePageResponse result = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime.AddMinutes(1));

        Assert.Equal(sensor.Id, result.SensorProfileId);
        Assert.Equal("Boolean", result.TelemetryDataType);
        Assert.Equal(100, result.Limit);
        Assert.Empty(result.Readings);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task Range_UnknownSensor_ReturnsNotFoundProblemDetails()
    {
        Guid missingSensor = Guid.NewGuid();
        using HttpResponseMessage response = await _client.GetAsync(
            RangeUrl(missingSensor, BaseTime, BaseTime.AddMinutes(1), 10));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("Sensor not found", problem.Title);
    }

    [Fact]
    public async Task Range_TwoSensorsRemainIsolatedAtIdenticalTimestamps()
    {
        SensorResponse first = await RegisterAsync("Integer", "A5");
        SensorResponse second = await RegisterAsync("Integer", "A6");

        await IngestAsync(first.Id, IntegerReading(17, BaseTime));
        await IngestAsync(second.Id, IntegerReading(91, BaseTime));
        await IngestAsync(first.Id, IntegerReading(18, BaseTime));

        TelemetryRangePageResponse firstPage = await GetRangeAsync(
            first.Id, BaseTime, BaseTime, 10);
        TelemetryRangePageResponse secondPage = await GetRangeAsync(
            second.Id, BaseTime, BaseTime, 10);

        Assert.Equal(new[] { 17, 18 },
            firstPage.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
        Assert.Equal(new[] { 91 },
            secondPage.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
        Assert.All(firstPage.Readings, reading =>
            Assert.Equal(first.Id, reading.SensorProfileId));
        Assert.All(secondPage.Readings, reading =>
            Assert.Equal(second.Id, reading.SensorProfileId));
    }

    [Fact]
    public async Task Range_OffsetEquivalentBoundaries_PreserveSevenDigitTickPrecision()
    {
        SensorResponse sensor = await RegisterAsync("Float", "A7");
        DateTimeOffset exact = BaseTime.AddTicks(1);
        DateTimeOffset next = BaseTime.AddTicks(2);

        await IngestAsync(sensor.Id,
            new IngestTelemetryRequest(exact.ToOffset(TimeSpan.FromHours(2)), 1.5, null, null));
        await IngestAsync(sensor.Id,
            new IngestTelemetryRequest(next, 2.5, null, null));

        TelemetryRangePageResponse exactPage = await GetRangeAsync(
            sensor.Id,
            exact.ToOffset(TimeSpan.FromHours(-3)),
            exact.ToOffset(TimeSpan.FromHours(5)), 10);

        TelemetryReadingResponse reading = Assert.Single(exactPage.Readings);
        Assert.Equal(1.5, reading.FloatValue);
        Assert.Equal(exact, reading.CapturedAtUtc);
        Assert.Equal(exact, exactPage.FromUtc);
        Assert.Equal(exact, exactPage.ToUtc);
        Assert.Null(exactPage.NextCursor);
    }

    [Fact]
    public async Task Range_RejectedJaggedBatch_DoesNotPartiallyChangeEitherHistoryView()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "A8");
        await IngestAsync(sensor.Id, IntegerReading(7, BaseTime));

        var invalid = new IngestTelemetryRequest(
            BaseTime.AddSeconds(2), 2.5, null, null);
        using HttpResponseMessage rejection = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest(
                [[IntegerReading(8, BaseTime.AddSeconds(1))],
                 [invalid, IntegerReading(9, BaseTime.AddSeconds(3))]]));
        Assert.Equal(HttpStatusCode.BadRequest, rejection.StatusCode);

        TelemetryRangePageResponse page = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime.AddMinutes(1), 10);
        TelemetryReadingResponse item = Assert.Single(page.Readings);
        Assert.Equal(7, item.IntegerValue);

        TelemetryReadingResponse[]? recent = await _client.GetFromJsonAsync<
            TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.NotNull(recent);
        Assert.Equal(7, Assert.Single(recent).IntegerValue);
    }

    [Fact]
    public async Task Range_NewAcceptedReadingAppearsOnNextQuery_WithoutChangingEarlierResponse()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "A9");
        await IngestAsync(sensor.Id, IntegerReading(1, BaseTime));

        TelemetryRangePageResponse initial = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime.AddSeconds(2), 10);
        await IngestAsync(sensor.Id, IntegerReading(2, BaseTime.AddSeconds(1)));
        TelemetryRangePageResponse later = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime.AddSeconds(2), 10);

        Assert.Equal(new[] { 1 },
            initial.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
        Assert.Equal(new[] { 1, 2 },
            later.Readings.Select(reading => reading.IntegerValue!.Value).ToArray());
    }

    [Fact]
    public async Task Range_ConcurrentHttpIngestion_ReturnsEveryAcceptedReadingOnce()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "AA");

        // HTTP submissions are intentionally concurrent. Ingestion's existing
        // per-sensor gate defines the actual accepted order, which the AVL
        // duplicate bucket must then reproduce exactly.
        Task[] submissions = Enumerable.Range(0, 32)
            .Select(index => IngestAsync(sensor.Id, IntegerReading(index, BaseTime)))
            .ToArray();
        await Task.WhenAll(submissions);

        TelemetryReadingResponse[]? recent = await _client.GetFromJsonAsync<
            TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.NotNull(recent);
        Assert.Equal(32, recent.Length);

        var chronological = new List<int>();
        DateTimeOffset? cursorTime = null;
        int? cursorSequence = null;
        do
        {
            TelemetryRangePageResponse page = await GetRangeAsync(
                sensor.Id, BaseTime, BaseTime, 11, cursorTime, cursorSequence);
            chronological.AddRange(page.Readings
                .Select(reading => reading.IntegerValue!.Value));

            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            Assert.NotNull(page.NextCursor);
            cursorTime = page.NextCursor.CapturedAtUtc;
            cursorSequence = page.NextCursor.Sequence;
            Assert.True(chronological.Count <= 32);
        }
        while (true);

        Assert.Equal(recent.Select(reading => reading.IntegerValue!.Value).ToArray(),
            chronological.ToArray());
        Assert.Equal(Enumerable.Range(0, 32).ToArray(),
            chronological.OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task Range_LastPacketCursor_ReturnsEmptyTerminalPage()
    {
        SensorResponse sensor = await RegisterAsync("Integer", "AB");
        await IngestAsync(sensor.Id, IntegerReading(3, BaseTime));

        TelemetryRangePageResponse page = await GetRangeAsync(
            sensor.Id, BaseTime, BaseTime, 10, BaseTime, 0);
        Assert.Empty(page.Readings);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("missingBoth")]
    [InlineData("missingStart")]
    [InlineData("missingEnd")]
    [InlineData("missingTimezone")]
    [InlineData("malformedTimestamp")]
    [InlineData("reversed")]
    [InlineData("zeroLimit")]
    [InlineData("overMaximumLimit")]
    [InlineData("blankLimit")]
    [InlineData("nonIntegerLimit")]
    [InlineData("cursorTimestampOnly")]
    [InlineData("cursorSequenceOnly")]
    [InlineData("negativeCursorSequence")]
    [InlineData("cursorOutsideRange")]
    [InlineData("fabricatedCursorTimestamp")]
    [InlineData("fabricatedCursorSequence")]
    public async Task Range_InvalidParameters_ReturnConsistentBadRequestProblemDetails(
        string scenario)
    {
        SensorResponse sensor = await RegisterAsync("Integer", "AC");
        await IngestAsync(sensor.Id, IntegerReading(1, BaseTime));

        string from = $"fromUtc={Encode(BaseTime)}";
        string to = $"toUtc={Encode(BaseTime.AddMinutes(1))}";
        string valid = $"{from}&{to}";

        string query = scenario switch
        {
            "missingBoth" => string.Empty,
            "missingStart" => to,
            "missingEnd" => from,
            "missingTimezone" => $"fromUtc=2026-11-10T08%3A00%3A00&{to}",
            "malformedTimestamp" => $"{from}&toUtc=not-a-date",
            "reversed" => $"fromUtc={Encode(BaseTime.AddMinutes(2))}&{to}",
            "zeroLimit" => $"{valid}&limit=0",
            "overMaximumLimit" => $"{valid}&limit=1001",
            "blankLimit" => $"{valid}&limit=",
            "nonIntegerLimit" => $"{valid}&limit=abc",
            "cursorTimestampOnly" => $"{valid}&afterUtc={Encode(BaseTime)}",
            "cursorSequenceOnly" => $"{valid}&afterSequence=0",
            "negativeCursorSequence" => $"{valid}&afterUtc={Encode(BaseTime)}&afterSequence=-1",
            "cursorOutsideRange" => $"{valid}&afterUtc={Encode(BaseTime.AddMinutes(2))}&afterSequence=0",
            "fabricatedCursorTimestamp" => $"{valid}&afterUtc={Encode(BaseTime.AddSeconds(1))}&afterSequence=0",
            "fabricatedCursorSequence" => $"{valid}&afterUtc={Encode(BaseTime)}&afterSequence=20",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        using HttpResponseMessage response = await _client.GetAsync(
            $"/api/sensors/{sensor.Id}/telemetry/range?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));

        // A failed read must never insert or remove telemetry.
        TelemetryReadingResponse[]? recent = await _client.GetFromJsonAsync<
            TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=10");
        Assert.NotNull(recent);
        Assert.Equal(1, Assert.Single(recent).IntegerValue);
    }

    private async Task<SensorResponse> RegisterAsync(string type, string lastMacOctet)
    {
        var request = new RegisterSensorRequest(
            $"P3 AVL {type} Sensor",
            $"AA:BB:CC:DD:EE:{lastMacOctet}",
            "MacAddress",
            new SensorDeploymentLocationRequest("Test Facility", "Lab", "Node"),
            "Environmental",
            type);

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse? sensor = await response.Content.ReadFromJsonAsync<SensorResponse>();
        return sensor ?? throw new InvalidOperationException("Registration returned no sensor.");
    }

    private async Task IngestAsync(Guid sensorId, IngestTelemetryRequest reading)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensorId}/telemetry", reading);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task IngestBatchAsync(
        Guid sensorId, IngestTelemetryRequest?[]?[] batches)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensorId}/telemetry/batches",
            new IngestTelemetryBatchRequest(batches));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<TelemetryRangePageResponse> GetRangeAsync(
        Guid sensorId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? limit = null,
        DateTimeOffset? afterUtc = null,
        int? afterSequence = null)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            RangeUrl(sensorId, from, to, limit, afterUtc, afterSequence));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        TelemetryRangePageResponse? result = await response.Content
            .ReadFromJsonAsync<TelemetryRangePageResponse>();
        return result ?? throw new InvalidOperationException("Range response was empty.");
    }

    private static string RangeUrl(
        Guid sensorId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? limit,
        DateTimeOffset? afterUtc = null,
        int? afterSequence = null)
    {
        string url = $"/api/sensors/{sensorId}/telemetry/range" +
            $"?fromUtc={Encode(from)}&toUtc={Encode(to)}";

        if (limit.HasValue)
        {
            url += $"&limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (afterUtc.HasValue)
        {
            url += $"&afterUtc={Encode(afterUtc.Value)}";
        }

        if (afterSequence.HasValue)
        {
            url += $"&afterSequence={afterSequence.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    private static string Encode(DateTimeOffset timestamp) =>
        Uri.EscapeDataString(timestamp.ToString("O", CultureInfo.InvariantCulture));

    private static IngestTelemetryRequest IntegerReading(
        int value, DateTimeOffset timestamp) =>
        new(timestamp, null, value, null);

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}