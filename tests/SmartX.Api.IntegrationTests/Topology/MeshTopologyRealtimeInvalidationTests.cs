using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Api.Dashboard;
using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Topology;

namespace SmartX.Api.IntegrationTests.Topology;

/// <summary>
/// End-to-end regression for P3-07 Commit 3. Every HTTP request uses the real
/// controller, in-memory sensor registry, and synchronized adjacency store.
/// Only the SignalR transport is replaced by an IRealtimeNotifier recorder.
/// Notifications must never become another authoritative graph store.
/// </summary>
public sealed class MeshTopologyRealtimeInvalidationTests : IDisposable
{
    private readonly NotificationFactory _factory = new();
    private readonly HttpClient _client;

    public MeshTopologyRealtimeInvalidationTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task LinkCreation_BroadcastsOneReason_AfterSuccessfulGraphWrite()
    {
        SensorResponse relay = await RegisterAsync(0x91);
        MeshLinkResponse link = await CreateLinkAsync(relay.Id, 1.25d);

        var notification = Assert.Single(TopologyEvents());
        Assert.Equal("topology-link-created", notification.Reason);
        Assert.Null(notification.DeviceId);
        Assert.NotEqual(Guid.Empty, link.Id);

        MeshTopologyResponse graph = await GetGraphAsync();
        Assert.Equal(1, graph.LinkCount);
        Assert.Contains(graph.Links, item => item.Id == link.Id);
    }

    [Fact]
    public async Task DuplicateLinkAndInvalidRequest_EmitNoTopologyChange()
    {
        SensorResponse relay = await RegisterAsync(0x92);
        await CreateLinkAsync(relay.Id, 2d);
        int before = TopologyEvents().Length;
        long revision = (await GetGraphAsync()).GraphVersion;

        using HttpResponseMessage duplicate = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest(SensorKey(relay.Id), "gateway", 2d));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using HttpResponseMessage invalidCost = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", SensorKey(relay.Id), -1d));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCost.StatusCode);

        Assert.Equal(before, TopologyEvents().Length);
        Assert.Equal(revision, (await GetGraphAsync()).GraphVersion);
    }

    [Fact]
    public async Task ChangedCost_InvalidatesOnce_EqualCostPatchDoesNot()
    {
        SensorResponse relay = await RegisterAsync(0x93);
        MeshLinkResponse link = await CreateLinkAsync(relay.Id, 3d);
        long before = (await GetGraphAsync()).GraphVersion;

        using HttpResponseMessage changed = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{link.Id:D}/cost",
            new UpdateMeshLinkCostRequest(1.5d));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(before + 1, (await GetGraphAsync()).GraphVersion);
        Assert.Single(TopologyEvents(), eventItem =>
            eventItem.Reason == "topology-link-cost-changed");

        using HttpResponseMessage noOp = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{link.Id:D}/cost",
            new UpdateMeshLinkCostRequest(1.5d));
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(before + 1, (await GetGraphAsync()).GraphVersion);
        Assert.Equal(1, TopologyEvents().Count(eventItem =>
            eventItem.Reason == "topology-link-cost-changed"));
    }

    [Fact]
    public async Task LinkRemoval_InvalidatesOnlyWhenRemovalSucceeds()
    {
        SensorResponse relay = await RegisterAsync(0x94);
        MeshLinkResponse link = await CreateLinkAsync(relay.Id, 2d);
        await SetStatusAsync(relay.Id, "Online");
        MeshDeviceRouteResponse beforeRemoval = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{relay.Id:D}/route");
        Assert.Equal("Reachable", beforeRemoval.Status);

        using HttpResponseMessage deleted = await _client.DeleteAsync(
            $"/api/topology/links/{link.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await GetGraphAsync()).Links);
        Assert.Single(TopologyEvents(), eventItem =>
            eventItem.Reason == "topology-link-removed");
        MeshDeviceRouteResponse route = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{relay.Id:D}/route");
        Assert.Equal("Disconnected", route.Status);
        Assert.Empty(route.Bfs.PathToGateway);

        using HttpResponseMessage missing = await _client.DeleteAsync(
            $"/api/topology/links/{link.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1, TopologyEvents().Count(eventItem =>
            eventItem.Reason == "topology-link-removed"));
    }

    [Fact]
    public async Task TopologyGets_ArePureReads_AndDoNotPublishEvents()
    {
        SensorResponse relay = await RegisterAsync(0x95);
        await CreateLinkAsync(relay.Id, 2d);
        int count = _factory.Notifier.Snapshot().Length;

        using HttpResponseMessage graph = await _client.GetAsync("/api/topology");
        using HttpResponseMessage reachability = await _client.GetAsync(
            "/api/topology/reachability");
        using HttpResponseMessage mst = await _client.GetAsync("/api/topology/mst");
        using HttpResponseMessage route = await _client.GetAsync(
            $"/api/topology/devices/{relay.Id:D}/route");

        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reachability.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mst.StatusCode);
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        Assert.Equal(count, _factory.Notifier.Snapshot().Length);
    }

    [Fact]
    public async Task ExistingRegistrationAndManualStatusEvents_InvalidateTopology()
    {
        SensorResponse sensor = await RegisterAsync(0x96);
        Assert.Single(_factory.Notifier.Snapshot(), message =>
            message.Reason == "sensor-registered" && message.DeviceId == sensor.Id);

        await CreateLinkAsync(sensor.Id, 1d);
        MeshTopologyResponse before = await GetGraphAsync();
        Assert.Equal("Offline", Assert.Single(before.Nodes,
            node => node.SensorProfileId == sensor.Id).Status);

        await SetStatusAsync(sensor.Id, "Online");
        MeshTopologyResponse after = await GetGraphAsync();
        Assert.True(after.GraphVersion > before.GraphVersion);
        Assert.Equal("Online", Assert.Single(after.Nodes,
            node => node.SensorProfileId == sensor.Id).Status);
        Assert.Single(_factory.Notifier.Snapshot(), message =>
            message.Reason == "sensor-status-changed" && message.DeviceId == sensor.Id);
        MeshDeviceRouteResponse liveRoute = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{sensor.Id:D}/route");
        Assert.Equal("Reachable", liveRoute.Status);
        Assert.Equal(1, liveRoute.Bfs.HopCount);

        // A repeated status request does not constitute a new transition.
        await SetStatusAsync(sensor.Id, "Online");
        Assert.Equal(1, _factory.Notifier.Snapshot().Count(message =>
            message.Reason == "sensor-status-changed" && message.DeviceId == sensor.Id));
    }

    [Fact]
    public async Task MetadataChange_UsesExistingSensorUpdatedReason_WithoutChangingGraphRevision()
    {
        SensorResponse sensor = await RegisterAsync(0x97);
        MeshTopologyResponse before = await GetGraphAsync();

        var request = new UpdateSensorRequest(
            "Renamed greenhouse relay",
            new SensorDeploymentLocationRequest("Greenhouse", "Hydroponics", "Rack 2"),
            "Environmental", "Float");
        using HttpResponseMessage update = await _client.PutAsJsonAsync(
            $"/api/sensors/{sensor.Id:D}", request);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal("Renamed greenhouse relay", Assert.Single(after.Nodes,
            node => node.SensorProfileId == sensor.Id).Name);
        Assert.Single(_factory.Notifier.Snapshot(), message =>
            message.Reason == "sensor-updated" && message.DeviceId == sensor.Id);
    }

    [Fact]
    public async Task AcceptedTelemetry_RevivesOfflineSensor_AndPublishesStatusReason()
    {
        SensorResponse sensor = await RegisterAsync(0x98);
        await CreateLinkAsync(sensor.Id, 2d);
        MeshTopologyResponse before = await GetGraphAsync();
        Assert.Equal("Offline", Assert.Single(before.Nodes,
            node => node.SensorProfileId == sensor.Id).Status);

        // Exercise the same coordinator invoked by the actual accepted-
        // telemetry observer; no topology-only sensor mutation is introduced.
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            DashboardEventCoordinator coordinator = scope.ServiceProvider
                .GetRequiredService<DashboardEventCoordinator>();
            await coordinator.AnalyzeAcceptedTelemetryAsync(
                sensor.Id, TelemetryAnomalyObservation.Stable);
        }

        MeshTopologyResponse after = await GetGraphAsync();
        Assert.True(after.GraphVersion > before.GraphVersion);
        Assert.Equal("Online", Assert.Single(after.Nodes,
            node => node.SensorProfileId == sensor.Id).Status);
        Assert.Single(_factory.Notifier.Snapshot(), message =>
            message.Reason == "sensor-status-changed" && message.DeviceId == sensor.Id);
        Assert.Single(_factory.Notifier.Snapshot(), message =>
            message.Reason == "telemetry-changed" && message.DeviceId == sensor.Id);

        // Further accepted packets leave an already-Online sensor's status
        // unchanged, so they do NOT cause a topology-relevant status signal.
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            DashboardEventCoordinator coordinator = scope.ServiceProvider
                .GetRequiredService<DashboardEventCoordinator>();
            await coordinator.AnalyzeAcceptedTelemetryAsync(
                sensor.Id, TelemetryAnomalyObservation.Stable);
        }
        Assert.Equal(1, _factory.Notifier.Snapshot().Count(message =>
            message.Reason == "sensor-status-changed" && message.DeviceId == sensor.Id));
    }

    [Fact]
    public async Task SignalRFailure_CannotRollBackSuccessfullyPersistedLink()
    {
        SensorResponse sensor = await RegisterAsync(0x99);
        _factory.Notifier.ThrowOnTopologyChange = true;

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", SensorKey(sensor.Id), 2.5d));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MeshLinkResponse link = Assert.IsType<MeshLinkResponse>(
            await response.Content.ReadFromJsonAsync<MeshLinkResponse>());
        MeshTopologyResponse graph = await GetGraphAsync();
        Assert.Contains(graph.Links, item => item.Id == link.Id);
        Assert.Single(TopologyEvents(), message =>
            message.Reason == "topology-link-created");
    }

    [Fact]
    public async Task UnknownLinks_DoNotPublishRemovalOrCostNotifications()
    {
        Guid unknown = Guid.NewGuid();
        using HttpResponseMessage patch = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{unknown:D}/cost",
            new UpdateMeshLinkCostRequest(2d));
        using HttpResponseMessage remove = await _client.DeleteAsync(
            $"/api/topology/links/{unknown:D}");
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        Assert.Empty(TopologyEvents());
    }

    private (string Reason, Guid? DeviceId)[] TopologyEvents() =>
        _factory.Notifier.Snapshot().Where(message =>
            message.Reason.StartsWith("topology-", StringComparison.Ordinal)).ToArray();

    private async Task<SensorResponse> RegisterAsync(byte suffix)
    {
        var request = new RegisterSensorRequest(
            $"Greenhouse relay {suffix:X2}",
            $"AA:BB:CC:DD:FA:{suffix:X2}",
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Greenhouse", "Hydroponics", "Rack 1"),
            "Environmental", "Float");
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<SensorResponse>(
            await response.Content.ReadFromJsonAsync<SensorResponse>());
    }

    private async Task<MeshLinkResponse> CreateLinkAsync(Guid sensorId, double cost)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", SensorKey(sensorId), cost));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<MeshLinkResponse>(
            await response.Content.ReadFromJsonAsync<MeshLinkResponse>());
    }

    private async Task SetStatusAsync(Guid sensorId, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{sensorId:D}/status", new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<MeshTopologyResponse> GetGraphAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/topology");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<MeshTopologyResponse>(
            await response.Content.ReadFromJsonAsync<MeshTopologyResponse>());
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static string SensorKey(Guid id) => $"sensor/{id:D}";

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class NotificationFactory : WebApplicationFactory<Program>
    {
        public RecordingNotifier Notifier { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(Notifier);
            });
        }
    }

    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly ConcurrentQueue<(string Reason, Guid? DeviceId)> _events = new();

        public bool ThrowOnTopologyChange { get; set; }

        public Task PublishAsync(string reason, Guid? sensorProfileId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue((reason, sensorProfileId));
            if (ThrowOnTopologyChange &&
                reason.StartsWith("topology-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Deliberately failed SignalR delivery after authoritative mutation.");
            }

            return Task.CompletedTask;
        }

        public (string Reason, Guid? DeviceId)[] Snapshot() => _events.ToArray();
    }
}