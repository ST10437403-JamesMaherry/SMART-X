using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using SmartX.Application.Topology;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Contracts.Topology;
using SmartX.Domain.Topology;

namespace SmartX.Api.IntegrationTests.Topology;

/// <summary>
/// Full HTTP verification for P3-07 read endpoints. Each test has its OWN
/// WebApplicationFactory, canonical sensor repository and shared mesh store.
/// Sensor/status changes use the existing HTTP API. Links are seeded through
/// the genuine store until simulation HTTP writes arrive in Commit 2.
/// </summary>
public sealed class MeshTopologyReadEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public MeshTopologyReadEndpointTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task EmptyRegistry_ExposesOnlyTheGateway_WithoutInventingLinks()
    {
        MeshTopologyResponse graph = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        MeshReachabilityResponse bfs = await ReadAsync<MeshReachabilityResponse>(
            "/api/topology/reachability");
        MeshMinimumSpanningTreeResponse mst =
            await ReadAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");

        Assert.Equal(0, graph.GraphVersion);
        Assert.Equal("gateway", graph.GatewayKey);
        Assert.Equal(1, graph.NodeCount);
        Assert.Equal(0, graph.LinkCount);
        MeshVertexResponse gateway = Assert.Single(graph.Nodes);
        Assert.Equal("gateway", gateway.Key);
        Assert.Equal("Gateway", gateway.Kind);
        Assert.Null(gateway.SensorProfileId);
        Assert.Null(gateway.Status);
        Assert.True(gateway.IsForwardingEligible);
        Assert.Empty(graph.Links);

        Assert.Equal(graph.GraphVersion, bfs.GraphVersion);
        Assert.Equal(0, bfs.RegisteredSensorCount);
        Assert.Equal(0, bfs.ReachableSensorCount);
        Assert.Empty(bfs.DisconnectedComponents);
        Assert.Empty(bfs.UnavailableSensorKeys);
        MeshHopCountResponse hop = Assert.Single(bfs.ReachableNodes);
        Assert.Equal("gateway", hop.NodeKey);
        Assert.Equal(0, hop.HopCount);

        Assert.Equal(graph.GraphVersion, mst.GraphVersion);
        Assert.Equal(0d, mst.TotalSimulatedEnergyCost);
        Assert.Equal(0, mst.SelectedEdgeCount);
        Assert.Empty(mst.SelectedEdges);
        Assert.Equal("gateway", Assert.Single(mst.ReachedNodeKeys));
    }

    [Fact]
    public async Task RegisteredSensor_AppearsWithRealMetadata_ButNoImplicitConnection()
    {
        SensorResponse sensor = await RegisterAsync(0x31, "Greenhouse moisture");

        MeshTopologyResponse graph = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        Assert.Equal(2, graph.NodeCount);
        Assert.Equal(0, graph.LinkCount);

        MeshVertexResponse vertex = Assert.Single(graph.Nodes, node =>
            node.SensorProfileId == sensor.Id);
        Assert.Equal(SensorKey(sensor.Id), vertex.Key);
        Assert.Equal("Greenhouse moisture", vertex.Name);
        Assert.Equal(sensor.DeviceIdentifier, vertex.DeviceIdentifier);
        Assert.Equal(sensor.IdentifierKind, vertex.IdentifierKind);
        Assert.Equal(sensor.Category, vertex.Category);
        Assert.Equal(sensor.TelemetryDataType, vertex.TelemetryDataType);
        Assert.Equal("Offline", vertex.Status);
        Assert.False(vertex.IsForwardingEligible);
        Assert.Equal("Greenhouse", vertex.DeploymentLocation?.Facility);

        MeshDeviceRouteResponse route = await ReadAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{sensor.Id:D}/route");
        Assert.Equal(graph.GraphVersion, route.GraphVersion);
        Assert.Equal("Unavailable", route.Status);
        Assert.Null(route.Bfs.HopCount);
        Assert.Empty(route.Bfs.PathToGateway);
        Assert.Null(route.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Empty(route.Mst.PathToGateway);

        // The P3-03 AVL history API is the selected sensor's authoritative
        // timeline. P3-07 must not introduce a duplicate telemetry index.
        string start = Uri.EscapeDataString("2026-11-10T00:00:00Z");
        string end = Uri.EscapeDataString("2026-11-11T00:00:00Z");
        TelemetryRangePageResponse history = await ReadAsync<TelemetryRangePageResponse>(
            $"/api/sensors/{sensor.Id:D}/telemetry/range?fromUtc={start}&toUtc={end}");
        Assert.Equal(sensor.Id, history.SensorProfileId);
        Assert.Empty(history.Readings);
    }

    [Fact]
    public async Task ConnectedNetwork_ExposesActualLinks_HopsAndWeightedTreeRoutes()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x32, "Online relay");
        SensorResponse leaf = await RegisterOnlineAsync(0x33, "Online leaf");

        MeshLink gatewayLink = await AddLinkAsync(
            MeshNodeKey.Gateway, MeshNodeKey.ForSensor(relay.Id), 1.25d);
        MeshLink leafLink = await AddLinkAsync(
            MeshNodeKey.ForSensor(relay.Id), MeshNodeKey.ForSensor(leaf.Id), 2.5d);

        MeshTopologyResponse graph = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        MeshReachabilityResponse bfs = await ReadAsync<MeshReachabilityResponse>(
            "/api/topology/reachability");
        MeshMinimumSpanningTreeResponse mst =
            await ReadAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");
        MeshDeviceRouteResponse route = await ReadAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{leaf.Id:D}/route");

        Assert.Equal(3, graph.NodeCount);
        Assert.Equal(2, graph.LinkCount);
        Assert.Equal(new[] { gatewayLink.Id, leafLink.Id }.OrderBy(id => id).ToArray(),
            graph.Links.Select(link => link.Id).OrderBy(id => id).ToArray());
        Assert.Contains(graph.Links, link =>
            link.Id == gatewayLink.Id && link.NodeAKey == "gateway" &&
            link.NodeBKey == SensorKey(relay.Id));
        Assert.Contains(graph.Links, link =>
            link.Id == leafLink.Id &&
            link.NodeAKey != link.NodeBKey &&
            new[] { link.NodeAKey, link.NodeBKey }.OrderBy(key => key)
                .SequenceEqual(new[] { SensorKey(relay.Id), SensorKey(leaf.Id) }
                    .OrderBy(key => key)));
        Assert.Equal(1.25d, graph.Links.Single(link =>
            link.Id == gatewayLink.Id).SimulatedEnergyCost);
        Assert.Equal(2.5d, graph.Links.Single(link =>
            link.Id == leafLink.Id).SimulatedEnergyCost);
        Assert.Equal(graph.GraphVersion, bfs.GraphVersion);
        Assert.Equal(graph.GraphVersion, mst.GraphVersion);
        Assert.Equal(graph.GraphVersion, route.GraphVersion);
        Assert.Equal(2, bfs.ReachableSensorCount);
        Assert.Equal(0, bfs.DisconnectedSensorCount);
        Assert.Equal(2, mst.SelectedEdgeCount);
        Assert.Equal(3.75d, mst.TotalSimulatedEnergyCost);
        Assert.Equal("Reachable", route.Status);

        string[] expectedPath = [SensorKey(leaf.Id), SensorKey(relay.Id), "gateway"];
        Assert.Equal(expectedPath, route.Bfs.PathToGateway);
        Assert.Equal(expectedPath, route.Mst.PathToGateway);
        Assert.Equal(2, route.Bfs.HopCount);
        Assert.Equal(2, route.Mst.HopCount);
        Assert.Equal(3.75d, route.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { leafLink.Id, gatewayLink.Id },
            route.Mst.SelectedLinkIdsToGateway);

        // Read endpoints must not advance the process-local graph revision.
        MeshTopologyResponse afterReads = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        Assert.Equal(graph.GraphVersion, afterReads.GraphVersion);
    }

    [Fact]
    public async Task BfsAndPrim_ExposeDifferentRoutes_WhenBackboneCostRequiresIt()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x34, "Cheap relay");
        SensorResponse leaf = await RegisterOnlineAsync(0x35, "Direct but expensive");
        await AddLinkAsync(MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(relay.Id), 1d);
        await AddLinkAsync(MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(leaf.Id), 9d);
        await AddLinkAsync(MeshNodeKey.ForSensor(relay.Id),
            MeshNodeKey.ForSensor(leaf.Id), 1d);

        MeshDeviceRouteResponse route = await ReadAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{leaf.Id:D}/route");
        MeshMinimumSpanningTreeResponse mst =
            await ReadAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");

        Assert.Equal("Reachable", route.Status);
        Assert.Equal(1, route.Bfs.HopCount);
        Assert.Equal(new[] { SensorKey(leaf.Id), "gateway" },
            route.Bfs.PathToGateway);
        Assert.Equal(2, route.Mst.HopCount);
        Assert.Equal(new[] { SensorKey(leaf.Id), SensorKey(relay.Id), "gateway" },
            route.Mst.PathToGateway);
        Assert.Equal(2d, route.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(2d, mst.TotalSimulatedEnergyCost);
        Assert.Equal(2, mst.SelectedEdgeCount);
    }

    [Fact]
    public async Task OfflineRelay_PreservesConfiguredLinks_ButDisconnectsItsLeaf()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x36, "Fragile bridge");
        SensorResponse leaf = await RegisterOnlineAsync(0x37, "Downstream leaf");
        await AddLinkAsync(MeshNodeKey.Gateway,
            MeshNodeKey.ForSensor(relay.Id), 1d);
        await AddLinkAsync(MeshNodeKey.ForSensor(relay.Id),
            MeshNodeKey.ForSensor(leaf.Id), 2d);

        MeshTopologyResponse before = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        await SetStatusAsync(relay.Id, "Offline");
        MeshTopologyResponse after = await ReadAsync<MeshTopologyResponse>(
            "/api/topology");
        MeshReachabilityResponse bfs = await ReadAsync<MeshReachabilityResponse>(
            "/api/topology/reachability");
        MeshMinimumSpanningTreeResponse mst =
            await ReadAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");
        MeshDeviceRouteResponse relayRoute = await ReadAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{relay.Id:D}/route");
        MeshDeviceRouteResponse leafRoute = await ReadAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{leaf.Id:D}/route");

        Assert.True(after.GraphVersion > before.GraphVersion);
        Assert.Equal(2, after.LinkCount);
        Assert.Equal("Offline", after.Nodes.Single(node =>
            node.SensorProfileId == relay.Id).Status);
        Assert.Equal("Unavailable", relayRoute.Status);
        Assert.Equal("Disconnected", leafRoute.Status);
        Assert.Empty(leafRoute.Bfs.PathToGateway);
        Assert.Empty(leafRoute.Mst.PathToGateway);
        Assert.Null(leafRoute.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(1, bfs.UnavailableSensorCount);
        Assert.Equal(1, bfs.DisconnectedSensorCount);
        Assert.Equal(SensorKey(leaf.Id), Assert.Single(
            Assert.Single(bfs.DisconnectedComponents).SensorKeys));
        Assert.Equal(0, mst.SelectedEdgeCount);
        Assert.Equal(0d, mst.TotalSimulatedEnergyCost);
    }

    [Fact]
    public async Task UnknownSensor_Returns404_AndEmptyGuidReturns400ProblemDetails()
    {
        Guid unknown = Guid.NewGuid();
        using HttpResponseMessage missing = await _client.GetAsync(
            $"/api/topology/devices/{unknown:D}/route");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json",
            missing.Content.Headers.ContentType?.MediaType);
        ProblemDetails? missingProblem = await missing.Content
            .ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(missingProblem);
        Assert.Equal(404, missingProblem.Status);

        using HttpResponseMessage invalid = await _client.GetAsync(
            $"/api/topology/devices/{Guid.Empty:D}/route");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json",
            invalid.Content.Headers.ContentType?.MediaType);
        ProblemDetails? invalidProblem = await invalid.Content
            .ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(invalidProblem);
        Assert.Equal(400, invalidProblem.Status);
    }

    private async Task<T> ReadAsync<T>(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? value = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(value);
    }

    private async Task<SensorResponse> RegisterAsync(byte lastOctet, string name)
    {
        var registration = new RegisterSensorRequest(
            name,
            $"AA:BB:CC:DD:EE:{lastOctet:X2}",
            "MacAddress",
            new SensorDeploymentLocationRequest("Greenhouse", "Hydroponics", "A1"),
            "Environmental",
            "Float");

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", registration);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse? sensor = await response.Content
            .ReadFromJsonAsync<SensorResponse>();
        return Assert.IsType<SensorResponse>(sensor);
    }

    private async Task<SensorResponse> RegisterOnlineAsync(byte lastOctet, string name)
    {
        SensorResponse sensor = await RegisterAsync(lastOctet, name);
        await SetStatusAsync(sensor.Id, "Online");
        return sensor;
    }

    private async Task SetStatusAsync(Guid id, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{id:D}/status", new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<MeshLink> AddLinkAsync(
        MeshNodeKey first, MeshNodeKey second, double simulatedEnergyCost)
    {
        IMeshTopologyStore store = _factory.Services
            .GetRequiredService<IMeshTopologyStore>();
        return await store.AddLinkAsync(
            new MeshLink(Guid.NewGuid(), first, second, simulatedEnergyCost));
    }

    private static string SensorKey(Guid id) => MeshNodeKey.ForSensor(id).ToString();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}