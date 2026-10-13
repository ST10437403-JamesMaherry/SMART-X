using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Api.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Exercises exact acceptance through HTTP. The recording buffer delegates to
/// the real implementation and retains an admission log even after the later
/// worker starts consuming, so these tests do not depend on pending counts.
/// </summary>
public sealed class TelemetryAcceptanceEndpointTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly AcceptanceApiFactory _factory = new();
    private readonly HttpClient _client;

    public TelemetryAcceptanceEndpointTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task SingleReading_Preserves201TypedBodyAndExactAdmission(string type)
    {
        var sensor = await RegisterAsync(type);
        var request = Reading(type, 0);
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TelemetryReadingResponse>();
        Assert.NotNull(body);
        Assert.Equal(sensor.Id, body.SensorProfileId);
        Assert.Equal(type, body.TelemetryDataType);
        Assert.Equal(request.FloatValue, body.FloatValue);
        Assert.Equal(request.IntegerValue, body.IntegerValue);
        Assert.Equal(request.BooleanValue, body.BooleanValue);

        var item = Assert.Single(_factory.Buffer.Accepted);
        Assert.Equal(sensor.Id, item.SensorProfileId);
        AssertPacket(request, item);
        var updated = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensor.Id}");
        Assert.Equal("Online", updated!.Status);
    }

    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task BatchOver100_QueuesAllPackets_PreservesJaggedOrderAndRecentLimit(string type)
    {
        var sensor = await RegisterAsync(type);
        var packets = Enumerable.Range(0, 151).Select(index => Reading(type, index)).ToArray();
        var request = new IngestTelemetryBatchRequest([packets[..71], [], packets[71..]]);
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry/batches", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchIngestionResponse>();
        Assert.NotNull(result);
        Assert.Equal(3, result.BatchCount);
        Assert.Equal(151, result.AcceptedPacketCount);

        var admitted = _factory.Buffer.Accepted;
        Assert.Equal(151, admitted.Length);
        for (var index = 0; index < packets.Length; index++) AssertPacket(packets[index], admitted[index]);

        var recent = await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
            $"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.NotNull(recent);
        Assert.Equal(100, recent.Length);
        Assert.Equal(packets.Skip(51).Select(packet => packet.CapturedAtUtc),
            recent.Select(packet => (DateTimeOffset?)packet.CapturedAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyBatch_CreatesNoWork_AndDoesNotPromoteOfflineSensor(bool innerBatches)
    {
        var sensor = await RegisterAsync("Integer");
        IngestTelemetryRequest?[]?[] batches = innerBatches ? [[], []] : [];
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest(batches));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchIngestionResponse>();
        Assert.Equal(0, result!.AcceptedPacketCount);
        Assert.Equal(innerBatches ? 2 : 0, result.BatchCount);
        Assert.Empty(_factory.Buffer.Accepted);
        var updated = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensor.Id}");
        Assert.Equal("Offline", updated!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSingleOrLateInvalidBatch_PersistsAndQueuesNothing(bool batch)
    {
        var sensor = await RegisterAsync("Integer");
        var invalid = new IngestTelemetryRequest(BaseTime, 1.25, 10, null);
        using var response = batch
            ? await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry/batches",
                new IngestTelemetryBatchRequest([[Reading("Integer", 0)], [invalid]]))
            : await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Buffer.Accepted);
        Assert.Empty((await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
            $"/api/sensors/{sensor.Id}/telemetry"))!);
    }

    [Fact]
    public async Task MissingSensor_QueuesNothing()
    {
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{Guid.NewGuid()}/telemetry",
            Reading("Integer", 0));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.Buffer.Accepted);
    }

    [Fact]
    public async Task CriticalThenStableReadings_KeepCapturedSeverity_AndRecoverAfterTwoObservations()
    {
        var sensor = await RegisterAsync("Integer");
        await PostIntegerAsync(sensor.Id, 10, 0);
        await PostIntegerAsync(sensor.Id, 30, 1);
        Assert.Equal(1, Assert.Single(ActiveSpikes()).OccurrenceCount);
        await PostIntegerAsync(sensor.Id, 31, 2);
        Assert.Single(ActiveSpikes());
        await PostIntegerAsync(sensor.Id, 32, 3);
        Assert.Empty(ActiveSpikes());

        var admitted = _factory.Buffer.Accepted;
        Assert.Equal(4, admitted.Length);
        Assert.Equal(DashboardIssueSeverity.Critical, admitted[1].Severity);
        Assert.Equal(30, Assert.IsType<TelemetryWorkItem<int>>(admitted[1]).Packet.Payload);
        Assert.Equal(DashboardIssueSeverity.Information, admitted[3].Severity);
    }

    [Fact]
    public async Task BatchMiddleSpike_IsQueuedButOnlyFinalPairChangesDashboard()
    {
        var sensor = await RegisterAsync("Integer");
        var values = new[] { 10, 100, 100, 100 };
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest([
                values.Select((value, index) => IntegerReading(value, index)).ToArray()]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(DashboardIssueSeverity.Critical, _factory.Buffer.Accepted[1].Severity);
        Assert.Empty(ActiveSpikes());
    }

    [Fact]
    public async Task RepeatedSpikes_CountEachSuccessfulRequestOnce()
    {
        var sensor = await RegisterAsync("Integer");
        foreach (var pair in new[] { (10, 0), (30, 1), (10, 2), (30, 3) })
        {
            await PostIntegerAsync(sensor.Id, pair.Item1, pair.Item2);
        }
        Assert.Equal(3, Assert.Single(ActiveSpikes()).OccurrenceCount);
        var issues = _factory.Services.GetRequiredService<DashboardIssueCollection>();
        Assert.Contains(issues.GetActiveSnapshot(DateTimeOffset.UtcNow),
            issue => issue.Type == DashboardIssueType.RepeatedFailure);
    }

    [Fact]
    public async Task ConcurrentSubmissions_ClassifyEachActualPredecessor_AndMatchPersistedOrder()
    {
        var sensor = await RegisterAsync("Integer");
        // Equal capture times make any serialized arrival order chronological.
        var submissions = Enumerable.Range(1, 32).Select(async value =>
        {
            using var response = await _client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry",
                IntegerReading(value % 2 == 0 ? 10 : 100, 0));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        });
        await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(20));

        var accepted = _factory.Buffer.Accepted.Select(item => Assert.IsType<TelemetryWorkItem<int>>(item)).ToArray();
        var recent = await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
            $"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.Equal(32, accepted.Length);
        Assert.Equal(recent!.Select(item => item.IntegerValue),
            accepted.Select(item => (int?)item.Packet.Payload));
        Assert.Equal(DashboardIssueSeverity.Information, accepted[0].Severity);
        for (var index = 1; index < accepted.Length; index++)
        {
            var expected = accepted[index].Packet.Payload == accepted[index - 1].Packet.Payload
                ? DashboardIssueSeverity.Information : DashboardIssueSeverity.Critical;
            Assert.Equal(expected, accepted[index].Severity);
        }
    }

    [Fact]
    public async Task SignalRFailure_DoesNotFailAcceptedTelemetryOrLoseQueuedWork()
    {
        using var factory = new AcceptanceApiFactory(failNotifications: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var sensor = await RegisterAsync(client, "Integer");
        using var response = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry",
            Reading("Integer", 0));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(factory.Buffer.Accepted);
        var updated = await client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensor.Id}");
        Assert.Equal("Online", updated!.Status);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private IEnumerable<DashboardIssue> ActiveSpikes() => _factory.Services
        .GetRequiredService<DashboardIssueCollection>().GetActiveSnapshot(DateTimeOffset.UtcNow)
        .Where(issue => issue.Type == DashboardIssueType.TelemetrySpike);

    private Task<SensorResponse> RegisterAsync(string type) => RegisterAsync(_client, type);

    private static async Task<SensorResponse> RegisterAsync(HttpClient client, string type)
    {
        using var response = await client.PostAsJsonAsync("/api/sensors", new RegisterSensorRequest(
            "Acceptance sensor", "acceptance-api-node", "UniqueIdentifier",
            new SensorDeploymentLocationRequest("Facility", "Zone", "Node"), "Environmental", type));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SensorResponse>())!;
    }

    private async Task PostIntegerAsync(Guid id, int value, int second)
    {
        using var response = await _client.PostAsJsonAsync($"/api/sensors/{id}/telemetry", IntegerReading(value, second));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static IngestTelemetryRequest IntegerReading(int value, int second)
        => new(BaseTime.AddSeconds(second), null, value, null);

    private static IngestTelemetryRequest Reading(string type, int index) => type switch
    {
        "Float" => new(BaseTime.AddSeconds(index), index + 0.25, null, null),
        "Integer" => IntegerReading(index, index),
        "Boolean" => new(BaseTime.AddSeconds(index), null, null, index % 2 != 0),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void AssertPacket(IngestTelemetryRequest expected, TelemetryWorkItem actual)
    {
        Assert.Equal(expected.CapturedAtUtc, actual.CapturedAtUtc);
        if (expected.FloatValue.HasValue)
            Assert.Equal(expected.FloatValue.Value, Assert.IsType<TelemetryWorkItem<double>>(actual).Packet.Payload);
        else if (expected.IntegerValue.HasValue)
            Assert.Equal(expected.IntegerValue.Value, Assert.IsType<TelemetryWorkItem<int>>(actual).Packet.Payload);
        else
            Assert.Equal(expected.BooleanValue!.Value, Assert.IsType<TelemetryWorkItem<bool>>(actual).Packet.Payload);
    }

    private sealed class AcceptanceApiFactory(bool failNotifications = false) : WebApplicationFactory<Program>
    {
        public RecordingBuffer Buffer { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITelemetryWorkBuffer>();
                services.AddSingleton<ITelemetryWorkBuffer>(Buffer);
                if (failNotifications)
                {
                    services.RemoveAll<IHubContext<DashboardHub, IDashboardClient>>();
                    services.AddSingleton<IHubContext<DashboardHub, IDashboardClient>>(new FailingHubContext());
                }
            });
        }
    }

    private sealed class RecordingBuffer : ITelemetryWorkBuffer
    {
        private readonly InMemoryTelemetryWorkBuffer _inner = new();
        private readonly object _syncRoot = new();
        private readonly List<TelemetryWorkItem> _accepted = [];
        public TelemetryWorkItem[] Accepted
        {
            get
            {
                lock (_syncRoot) return _accepted.OrderBy(item => item.ArrivalSequence).ToArray();
            }
        }

        public TelemetryWorkItem<T> Enqueue<T>(Guid sensorProfileId, TelemetryPacket<T> packet,
            DashboardIssueSeverity severity, DateTimeOffset acceptedAtUtc) where T : struct
        {
            var item = _inner.Enqueue(sensorProfileId, packet, severity, acceptedAtUtc);
            lock (_syncRoot) _accepted.Add(item);
            return item;
        }

        public ValueTask<TelemetryDispatch> DequeueAsync(CancellationToken cancellationToken = default)
            => _inner.DequeueAsync(cancellationToken);
        public void SetOrdinaryDispatchPaused(bool paused) => _inner.SetOrdinaryDispatchPaused(paused);
        public TelemetryWorkBufferSnapshot GetSnapshot() => _inner.GetSnapshot();
    }

    private sealed class FailingHubContext : IHubContext<DashboardHub, IDashboardClient>
    {
        public IHubClients<IDashboardClient> Clients { get; } = new FailingHubClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FailingHubClients : IHubClients<IDashboardClient>
    {
        public IDashboardClient All { get; } = new FailingClient();
        public IDashboardClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IDashboardClient Client(string connectionId) => All;
        public IDashboardClient Clients(IReadOnlyList<string> connectionIds) => All;
        public IDashboardClient Group(string groupName) => All;
        public IDashboardClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IDashboardClient Groups(IReadOnlyList<string> groupNames) => All;
        public IDashboardClient User(string userId) => All;
        public IDashboardClient Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class FailingClient : IDashboardClient
    {
        public Task DashboardChanged(DashboardChangedResponse change)
            => Task.FromException(new InvalidOperationException("Injected notification failure."));
    }
}