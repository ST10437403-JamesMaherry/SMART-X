using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using SmartX.Api.Realtime;
using SmartX.Application.Realtime;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

public sealed class TelemetryPriorityCompatibilityTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OrdinaryAThenB_DispatchInFifoOrder()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using var client = Client(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client);
        var a = await PostAsync(client, sensor.Id, Integer(10, 0));
        var b = await PostAsync(client, sensor.Id, Integer(11, 1));
        var pending = await StateAsync(client);
        Assert.Equal(2, pending.OrdinaryPendingCount);
        Assert.Equal(0, pending.DispatchedCount);
        await PauseAsync(client, false);
        var result = await WaitAsync(client, state => state.ProcessedCount == 2);
        Assert.Equal(new long[] { 1, 2 }, result.RecentDispatches.Select(record => record.EnqueueSequence));
        Assert.Equal(new[] { a, b }, result.RecentDispatches.Select(record => record.Telemetry));
        Assert.All(result.RecentDispatches, record => Assert.Equal("Ordinary", record.Lane));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCriticalC_BypassesPendingAAndB_WithoutOverwritingNewerObservation(bool equalCaptureTime)
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using var client = Client(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client);
        var a = await PostAsync(client, sensor.Id, Integer(10, 0));
        var b = await PostAsync(client, sensor.Id, Integer(11, equalCaptureTime ? 0 : 1));
        var pending = await StateAsync(client);
        Assert.Equal(2, pending.AcceptedCount);
        Assert.Equal(2, pending.OrdinaryPendingCount);
        Assert.Equal(0, pending.DispatchedCount);
        Assert.Empty(pending.RecentDispatches);

        var c = await PostAsync(client, sensor.Id, Integer(100, equalCaptureTime ? 0 : 2));
        var bypass = await WaitAsync(client, state => state.ProcessedCount == 1);
        var first = Assert.Single(bypass.RecentDispatches);
        Assert.Equal(3, first.EnqueueSequence);
        Assert.Equal(1, first.DequeueSequence);
        Assert.Equal("Critical", first.Lane);
        Assert.Equal("Critical", first.Severity);
        Assert.Equal(c, first.Telemetry);
        Assert.Equal(2, bypass.OrdinaryPendingCount);
        Assert.True(bypass.OrdinaryDispatchPaused);

        await PauseAsync(client, false);
        var result = await WaitAsync(client, state => state.ProcessedCount == 3);
        Assert.Equal(new long[] { 3, 1, 2 }, result.RecentDispatches.Select(record => record.EnqueueSequence));
        Assert.Equal(new long[] { 1, 2, 3 }, result.RecentDispatches.Select(record => record.DequeueSequence));
        Assert.Equal(new[] { c, a, b }, result.RecentDispatches.Select(record => record.Telemetry));
        Assert.Equal(new[] { true, false, false }, result.RecentDispatches.Select(record => record.UpdatedLatestObservation));
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, result.OutstandingDispatchedCount);
        var snapshot = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        var device = Assert.Single(snapshot!.Devices);
        Assert.Equal("Online", device.Status);
        Assert.Equal(c, device.LatestProcessedTelemetry);
        var history = await client.GetFromJsonAsync<TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry");
        Assert.Equal(new[] { a, b, c }, history);
    }

    [Fact]
    public async Task EqualCriticalPriorities_KeepArrivalOrder_AndCannotPreemptExecutingOrdinaryWork()
    {
        await using var factory = new TelemetryProcessingScenarioFactory(blockFirst: true);
        using var client = Client(factory);
        var sensor = await RegisterAsync(client);
        await PostAsync(client, sensor.Id, Integer(10, 0));
        await factory.Probe.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await PostAsync(client, sensor.Id, Integer(11, 1));
        await PostAsync(client, sensor.Id, Integer(100, 2));
        await PostAsync(client, sensor.Id, Integer(1, 3));
        await PostAsync(client, sensor.Id, Integer(100, 4));
        var held = await StateAsync(client);
        Assert.Equal(1, held.DispatchedCount);
        Assert.Equal(0, held.ProcessedCount);
        Assert.Equal(1, held.OrdinaryPendingCount);
        Assert.Equal(3, held.CriticalPendingCount);
        Assert.Equal("Ordinary", held.CurrentDispatch!.Lane);
        factory.Probe.ReleaseFirst.SetResult();
        var result = await WaitAsync(client, state => state.ProcessedCount == 5);
        Assert.Equal(new long[] { 1, 3, 4, 5, 2 }, result.RecentDispatches.Select(record => record.EnqueueSequence));
        Assert.Equal(new long[] { 3, 4, 5 }, result.RecentDispatches.Where(record => record.Lane == "Critical").Select(record => record.EnqueueSequence));
        Assert.Equal(1, factory.Probe.MaximumActive);
    }

    [Fact]
    public async Task ConcurrentHttpSubmissions_AreProcessedOnceInOrdinaryArrivalOrder()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using var client = Client(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 48).Select(async index =>
        {
            await start.Task;
            return await PostAsync(client, sensor.Id, Integer(1000 + index, 0));
        }).ToArray();
        start.SetResult();
        var accepted = await Task.WhenAll(requests);
        var pending = await StateAsync(client);
        Assert.Equal(48, pending.AcceptedCount);
        Assert.Equal(48, pending.OrdinaryPendingCount);
        Assert.Equal(0, pending.DispatchedCount);
        await PauseAsync(client, false);
        var result = await WaitAsync(client, state => state.ProcessedCount == 48);
        Assert.Equal(Enumerable.Range(1, 48).Select(value => (long)value),
            result.RecentDispatches.Select(record => record.EnqueueSequence));
        Assert.Equal(accepted.Select(reading => reading.IntegerValue).Order(),
            result.RecentDispatches.Select(record => record.Telemetry.IntegerValue).Order());
        var history = await client.GetFromJsonAsync<TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.Equal(history, result.RecentDispatches.Select(record => record.Telemetry));
        var snapshot = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        Assert.Equal(result.RecentDispatches[^1].Telemetry, Assert.Single(snapshot!.Devices).LatestProcessedTelemetry);
        Assert.Equal(1, factory.Probe.MaximumActive);
    }

    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task JaggedBatchOver100_EveryExactPacketCompletesDespiteBothPublicHistoryLimits(string type)
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using var client = Client(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client, type);
        var packets = Enumerable.Range(0, 151).Select(index => Reading(type, index)).ToArray();
        using var response = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry/batches",
            new IngestTelemetryBatchRequest([packets[..71], [], packets[71..]]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TelemetryBatchIngestionResponse>();
        Assert.Equal(151, body!.AcceptedPacketCount);
        Assert.Equal(3, body.BatchCount);
        var pending = await StateAsync(client);
        Assert.Equal(151, pending.AcceptedCount);
        Assert.Equal(151, pending.OrdinaryPendingCount);
        Assert.Equal(0, pending.DispatchedCount);
        await PauseAsync(client, false);
        var result = await WaitAsync(client, state => state.ProcessedCount == 151);
        await WaitForProbeAsync(factory.Probe, 151);
        var completed = factory.Probe.Completed;
        Assert.Equal(151, completed.Length);
        Assert.Equal(Enumerable.Range(1, 151).Select(value => (long)value),
            completed.Select(dispatch => dispatch.WorkItem.ArrivalSequence));
        for (var index = 0; index < packets.Length; index++)
        {
            // This is the entire worker completion log, not the capped HTTP
            // projection. Values are compared against the original requests.
            var actual = TelemetryProcessingMapper.ToReading(completed[index].WorkItem);
            Assert.Equal(sensor.Id, actual.SensorProfileId);
            Assert.Equal(type, actual.TelemetryDataType);
            Assert.Equal(packets[index].CapturedAtUtc, actual.CapturedAtUtc);
            Assert.Equal(packets[index].FloatValue, actual.FloatValue);
            Assert.Equal(packets[index].IntegerValue, actual.IntegerValue);
            Assert.Equal(packets[index].BooleanValue, actual.BooleanValue);
        }
        Assert.Equal(100, result.RecentDispatches.Count);
        Assert.Equal(52, result.RecentDispatches[0].EnqueueSequence);
        Assert.Equal(151, result.DispatchedCount);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, result.OutstandingDispatchedCount);
        var history = await client.GetFromJsonAsync<TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry?limit=100");
        Assert.Equal(packets.Skip(51).Select(packet => packet.CapturedAtUtc), history!.Select(packet => (DateTimeOffset?)packet.CapturedAtUtc));
        Assert.Equal(history, result.RecentDispatches.Select(record => record.Telemetry));
    }

    [Fact]
    public async Task AnomalyRecovery_AtAcceptanceIsNotReplayedByDelayedProcessing()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using var client = Client(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client);
        await PostAsync(client, sensor.Id, Integer(10, 0));
        await PostAsync(client, sensor.Id, Integer(11, 1));
        await PostAsync(client, sensor.Id, Integer(100, 2));
        await WaitAsync(client, state => state.ProcessedCount == 1);
        var spike = Assert.Single((await DashboardAsync(client)).Issues, issue => issue.Type == "TelemetrySpike");
        Assert.Equal("Critical", spike.Severity);
        Assert.Equal(1, spike.OccurrenceCount);
        await PostAsync(client, sensor.Id, Integer(101, 3));
        Assert.Single((await DashboardAsync(client)).Issues, issue => issue.Type == "TelemetrySpike");
        await PostAsync(client, sensor.Id, Integer(102, 4));
        Assert.DoesNotContain((await DashboardAsync(client)).Issues, issue => issue.Type == "TelemetrySpike");
        var pending = await StateAsync(client);
        Assert.Equal(4, pending.OrdinaryPendingCount);
        await PauseAsync(client, false);
        var result = await WaitAsync(client, state => state.ProcessedCount == 5);
        Assert.Equal(new long[] { 3, 1, 2, 4, 5 }, result.RecentDispatches.Select(record => record.EnqueueSequence));
        Assert.DoesNotContain((await DashboardAsync(client)).Issues, issue => issue.Type == "TelemetrySpike");
        var snapshot = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        Assert.Equal(102, Assert.Single(snapshot!.Devices).LatestProcessedTelemetry!.IntegerValue);
    }

    [Fact]
    public async Task WorkerFailure_RemainsVisibleOverHttp_AndRetainsFailedAndSubsequentAcceptedWork()
    {
        await using var factory = new TelemetryProcessingScenarioFactory(blockFirst: true, failFirst: true);
        using var client = Client(factory);
        var sensor = await RegisterAsync(client);
        var a = await PostAsync(client, sensor.Id, Integer(10, 0));
        await factory.Probe.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await PostAsync(client, sensor.Id, Integer(11, 1));
        await PostAsync(client, sensor.Id, Integer(100, 2));
        factory.Probe.ReleaseFirst.SetResult();
        var worker = factory.Services.GetServices<IHostedService>().OfType<TelemetryProcessingWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var failed = await StateAsync(client);
        Assert.Equal("Faulted", failed.WorkerStatus);
        Assert.NotNull(failed.Failure);
        Assert.DoesNotContain("private test details", failed.Failure);
        Assert.Equal(a, failed.CurrentDispatch!.Telemetry);
        Assert.Equal("Failed", failed.CurrentDispatch.Outcome);
        Assert.Equal(0, failed.ProcessedCount);
        Assert.Equal(1, failed.DispatchedCount);
        Assert.Equal(2, failed.PendingCount);
        Assert.Equal(1, failed.OutstandingDispatchedCount);
        var store = factory.Services.GetRequiredService<ITelemetryProcessingStore>();
        Assert.Same(factory.Probe.Started[0], store.GetSnapshot().CurrentDispatch);
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await PostAsync(client, sensor.Id, Integer(101, 3));
        var after = await StateAsync(client);
        Assert.Equal("Faulted", after.WorkerStatus);
        Assert.Equal(4, after.AcceptedCount);
        Assert.Equal(3, after.PendingCount);
        Assert.Equal(1, after.DispatchedCount);
        Assert.Empty(factory.Probe.Completed);
        var history = await client.GetFromJsonAsync<TelemetryReadingResponse[]>($"/api/sensors/{sensor.Id}/telemetry");
        Assert.Equal(new int?[] { 10, 11, 100, 101 }, history!.Select(reading => reading.IntegerValue));
    }

    [Fact]
    public async Task HostShutdown_RetainsInterruptedDispatchAndBothQueuedLanes()
    {
        await using var factory = new TelemetryProcessingScenarioFactory(blockFirst: true);
        using var client = Client(factory);
        var sensor = await RegisterAsync(client);
        await PostAsync(client, sensor.Id, Integer(10, 0));
        await factory.Probe.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await PostAsync(client, sensor.Id, Integer(11, 1));
        await PostAsync(client, sensor.Id, Integer(100, 2));
        var store = factory.Services.GetRequiredService<ITelemetryProcessingStore>();
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var state = store.GetSnapshot();
        Assert.Equal(TelemetryWorkerStatus.Stopped, state.WorkerStatus);
        Assert.Equal(3, state.Buffer.AcceptedCount);
        Assert.Equal(1, state.Buffer.DispatchedCount);
        Assert.Equal(0, state.ProcessedCount);
        Assert.Equal(1, state.Buffer.OrdinaryPendingCount);
        Assert.Equal(1, state.Buffer.CriticalPendingCount);
        Assert.Equal(1, state.OutstandingDispatchedCount);
        Assert.Same(factory.Probe.Started[0], state.CurrentDispatch);
        Assert.Equal(TelemetryDispatchOutcome.Interrupted, Assert.Single(state.RecentDispatches).Outcome);
        Assert.Empty(factory.Probe.Completed);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<SensorResponse> RegisterAsync(HttpClient client, string type = "Integer")
    {
        using var response = await client.PostAsJsonAsync("/api/sensors", new RegisterSensorRequest(
            "Priority test", "priority-node", "UniqueIdentifier",
            new SensorDeploymentLocationRequest("Facility", "Zone", "Node"), "Environmental", type));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SensorResponse>())!;
    }

    private static IngestTelemetryRequest Integer(int value, int second)
        => new(Captured.AddSeconds(second), null, value, null);

    private static IngestTelemetryRequest Reading(string type, int index) => type switch
    {
        "Float" => new(Captured.AddSeconds(index), 1000.25 + index, null, null),
        "Integer" => Integer(1000 + index, index),
        "Boolean" => new(Captured.AddSeconds(index), null, null, index % 2 != 0),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static async Task<TelemetryReadingResponse> PostAsync(HttpClient client, Guid id, IngestTelemetryRequest request)
    {
        using var response = await client.PostAsJsonAsync($"/api/sensors/{id}/telemetry", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TelemetryReadingResponse>())!;
    }

    private static async Task PauseAsync(HttpClient client, bool paused)
    {
        using var response = await client.PutAsJsonAsync("/api/realtime/simulation/ordinary-dispatch", new SetOrdinaryDispatchRequest(paused));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<TelemetryProcessingResponse> StateAsync(HttpClient client, CancellationToken token = default)
    {
        var state = await client.GetFromJsonAsync<TelemetryProcessingResponse>("/api/realtime/processing", token);
        Assert.NotNull(state);
        Assert.Equal(state.AcceptedCount, state.PendingCount + state.ProcessedCount + state.OutstandingDispatchedCount);
        Assert.Equal(state.DispatchedCount, state.ProcessedCount + state.OutstandingDispatchedCount);
        return state;
    }

    private static async Task<TelemetryProcessingResponse> WaitAsync(HttpClient client, Func<TelemetryProcessingResponse, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await StateAsync(client, timeout.Token);
            Assert.Null(state.Failure);
            if (predicate(state)) return state;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForProbeAsync(TelemetryProcessingProbe probe, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (probe.Completed.Length != count) await Task.Delay(10, timeout.Token);
    }

    private static async Task<DashboardSnapshotResponse> DashboardAsync(HttpClient client)
        => (await client.GetFromJsonAsync<DashboardSnapshotResponse>("/api/dashboard/issues"))!;
}