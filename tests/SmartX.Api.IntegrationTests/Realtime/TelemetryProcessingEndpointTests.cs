using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Api.Dashboard;
using SmartX.Api.Realtime;
using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Telemetry;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Realtime;

public sealed class TelemetryProcessingEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task PausedOrdinaryWork_RemainsAcceptedAndOnline_ThenProcessesWithItsNativeType(string type)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = CreateClient(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client, type);
        var request = Reading(type, 0);
        using var response = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<TelemetryReadingResponse>();
        var before = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        Assert.NotNull(before);
        Assert.Equal(1, before.Processing.AcceptedCount);
        Assert.Equal(1, before.Processing.OrdinaryPendingCount);
        Assert.Equal(0, before.Processing.ProcessedCount);
        var pendingDevice = Assert.Single(before.Devices);
        Assert.Equal("Online", pendingDevice.Status);
        Assert.Null(pendingDevice.LatestProcessedTelemetry);

        await PauseAsync(client, false);
        var processing = await WaitForProcessedAsync(client, 1);
        var after = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        var device = Assert.Single(after!.Devices);
        Assert.Equal(accepted, device.LatestProcessedTelemetry);
        Assert.NotNull(device.LastProcessedAtUtc);
        var dispatch = Assert.Single(processing.RecentDispatches);
        Assert.Equal(1, dispatch.EnqueueSequence);
        Assert.Equal(1, dispatch.DequeueSequence);
        Assert.Equal("Ordinary", dispatch.Lane);
        Assert.Equal("Processed", dispatch.Outcome);
        Assert.Equal(accepted, dispatch.Telemetry);
        Assert.True(dispatch.UpdatedLatestObservation);
        AssertAccounting(processing);
    }

    [Fact]
    public async Task Pause_LeavesCriticalLaneLive_AndResumeDrainsPendingOrdinaryWork()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = CreateClient(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client, "Integer");
        using var ordinary = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", Reading("Integer", 0));
        using var critical = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry",
            new IngestTelemetryRequest(Now.AddSeconds(1), null, 30, null));
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.Created, critical.StatusCode);
        var first = await WaitForProcessedAsync(client, 1);
        Assert.True(first.OrdinaryDispatchPaused);
        Assert.Equal(1, first.OrdinaryPendingCount);
        Assert.Equal("Critical", Assert.Single(first.RecentDispatches).Lane);
        Assert.Equal(2, first.RecentDispatches[0].EnqueueSequence);
        await PauseAsync(client, false);
        var completed = await WaitForProcessedAsync(client, 2);
        Assert.Equal(new long[] { 2, 1 }, completed.RecentDispatches.Select(item => item.EnqueueSequence));
        var snapshot = await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot");
        Assert.Equal(30, Assert.Single(snapshot!.Devices).LatestProcessedTelemetry!.IntegerValue);
        Assert.False(completed.RecentDispatches[1].UpdatedLatestObservation);
        AssertAccounting(completed);
    }

    [Fact]
    public async Task DelayedWork_DoesNotUndoExplicitStatusOrMetadataChanges()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = CreateClient(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client, "Integer");
        using var reading = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", Reading("Integer", 0));
        Assert.Equal(HttpStatusCode.Created, reading.StatusCode);
        using var metadata = await client.PutAsJsonAsync($"/api/sensors/{sensor.Id}", new UpdateSensorRequest(
            "Updated processing sensor", new SensorDeploymentLocationRequest("New Facility", "Zone", "Node"),
            "Environmental", "Integer"));
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var status = await client.PatchAsJsonAsync($"/api/sensors/{sensor.Id}/status", new ChangeSensorStatusRequest("Decommissioned"));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        await PauseAsync(client, false);
        await WaitForProcessedAsync(client, 1);
        var device = Assert.Single((await client.GetFromJsonAsync<RealtimeSnapshotResponse>("/api/realtime/snapshot"))!.Devices);
        Assert.Equal("Decommissioned", device.Status);
        Assert.Equal("Updated processing sensor", device.Name);
        Assert.Equal("New Facility", device.DeploymentLocation.Facility);
        Assert.Equal(10, device.LatestProcessedTelemetry!.IntegerValue);
    }

    [Fact]
    public async Task AcceptedWork_SurvivesCancellationOfItsApplicationCaller_AndScopeDisposal()
    {
        using var requestCancellation = new CancellationTokenSource();
        using var factory = new CancellationFactory(requestCancellation);
        using var client = CreateClient(factory);
        await PauseAsync(client, true);
        var sensor = await RegisterAsync(client, "Integer");
        // Exercise the same scoped application method used by the HTTP action.
        // The acceptance observer cancels the caller after admission, before
        // that method returns. Processing starts after the scope is disposed.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<SensorTelemetryService>();
            var response = await ingestion.IngestAsync(sensor.Id, Reading("Integer", 0), requestCancellation.Token);
            Assert.Equal(10, response.IntegerValue);
        }
        Assert.True(requestCancellation.IsCancellationRequested);
        await PauseAsync(client, false);
        var processing = await WaitForProcessedAsync(client, 1);
        Assert.Null(processing.Failure);
        Assert.Equal("Processed", Assert.Single(processing.RecentDispatches).Outcome);
    }

    [Fact]
    public async Task RealHubFailure_DoesNotFailAcceptedHttpResponseOrWorkerCompletion()
    {
        using var factory = new FailingHubFactory();
        using var client = CreateClient(factory);
        var sensor = await RegisterAsync(client, "Integer");
        using var response = await client.PostAsJsonAsync($"/api/sensors/{sensor.Id}/telemetry", Reading("Integer", 0));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var processing = await WaitForProcessedAsync(client, 1);
        Assert.Null(processing.Failure);
        AssertAccounting(processing);
    }

    [Fact]
    public async Task RealtimeHubNegotiation_UsesExistingSignalRHost()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = CreateClient(factory);
        using var response = await client.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("connectionToken", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"paused\":null}")]
    [InlineData("{\"paused\":\"yes\"}")]
    public async Task InvalidSimulationRequest_DoesNotChangePauseState(string json)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = CreateClient(factory);
        await PauseAsync(client, true);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PutAsync("/api/realtime/simulation/ordinary-dispatch", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var state = await client.GetFromJsonAsync<TelemetryProcessingResponse>("/api/realtime/processing");
        Assert.True(state!.OrdinaryDispatchPaused);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<SensorResponse> RegisterAsync(HttpClient client, string type)
    {
        using var response = await client.PostAsJsonAsync("/api/sensors", new RegisterSensorRequest(
            "Processing sensor", "processing-api-node", "UniqueIdentifier",
            new SensorDeploymentLocationRequest("Facility", "Zone", "Node"), "Environmental", type));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SensorResponse>())!;
    }

    private static IngestTelemetryRequest Reading(string type, int second) => type switch
    {
        "Float" => new(Now.AddSeconds(second), 10.25, null, null),
        "Integer" => new(Now.AddSeconds(second), null, 10, null),
        "Boolean" => new(Now.AddSeconds(second), null, null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static async Task PauseAsync(HttpClient client, bool paused)
    {
        using var response = await client.PutAsJsonAsync("/api/realtime/simulation/ordinary-dispatch",
            new SetOrdinaryDispatchRequest(paused));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<TelemetryProcessingResponse>();
        Assert.Equal(paused, state!.OrdinaryDispatchPaused);
    }

    private static async Task<TelemetryProcessingResponse> WaitForProcessedAsync(HttpClient client, long count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await client.GetFromJsonAsync<TelemetryProcessingResponse>("/api/realtime/processing", timeout.Token);
            Assert.NotNull(state);
            AssertAccounting(state);
            Assert.Null(state.Failure);
            if (state.ProcessedCount >= count) return state;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void AssertAccounting(TelemetryProcessingResponse state)
    {
        Assert.Equal(state.AcceptedCount, state.PendingCount + state.ProcessedCount + state.OutstandingDispatchedCount);
        Assert.Equal(state.DispatchedCount, state.ProcessedCount + state.OutstandingDispatchedCount);
    }

    private sealed class CancellationFactory(CancellationTokenSource cancellation) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITelemetryAcceptanceObserver>();
                services.AddScoped<ITelemetryAcceptanceObserver>(provider => new CancellingObserver(
                    cancellation, provider.GetRequiredService<DashboardEventCoordinator>()));
            });
        }
    }

    private sealed class CancellingObserver(CancellationTokenSource cancellation, DashboardEventCoordinator coordinator)
        : ITelemetryAcceptanceObserver
    {
        public Task ObserveAsync(Guid sensorProfileId, TelemetryAnomalyObservation observation)
        {
            cancellation.Cancel();
            return coordinator.AnalyzeAcceptedTelemetryAsync(sensorProfileId, observation);
        }
    }

    private sealed class FailingHubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHubContext<RealtimeHub>>();
                services.AddSingleton<IHubContext<RealtimeHub>>(new FailingHubContext());
            });
        }
    }

    private sealed class FailingHubContext : IHubContext<RealtimeHub>
    {
        public IHubClients Clients => throw new InvalidOperationException("Injected hub failure.");
        public IGroupManager Groups => throw new NotSupportedException();
    }
}