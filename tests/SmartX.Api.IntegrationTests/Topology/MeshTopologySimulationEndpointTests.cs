using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using SmartX.Contracts.Sensors;
using SmartX.Contracts.Topology;
using SmartX.Domain.Topology;

namespace SmartX.Api.IntegrationTests.Topology;

/// <summary>
/// P3-07 Commit 2 end-to-end HTTP tests. Each test owns an isolated
/// WebApplicationFactory and real process-local sensor + adjacency stores.
/// The only way tests add/change/remove links is through the NEW HTTP API;
/// sensor registration and operational health use the EXISTING HTTP API.
/// </summary>
public sealed class MeshTopologySimulationEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public MeshTopologySimulationEndpointTests()
    {
        _factory = CreateFactory("Development", simulationOptIn: false);
        _client = CreateClient(_factory);
    }

    [Fact]
    public async Task CreateLinks_UsesRealRegistrations_AndUpdatesBothGraphAlgorithms()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x81, "Relay");
        SensorResponse leaf = await RegisterOnlineAsync(0x82, "Moisture");
        MeshTopologyResponse before = await GetGraphAsync();

        MeshLinkResponse gatewayLink = await CreateLinkAsync(
            "gateway", SensorKey(relay.Id), 1.25d);
        MeshLinkResponse leafLink = await CreateLinkAsync(
            SensorKey(relay.Id), SensorKey(leaf.Id), 2.5d);

        MeshTopologyResponse graph = await GetGraphAsync();
        MeshReachabilityResponse bfs = await GetAsync<MeshReachabilityResponse>(
            "/api/topology/reachability");
        MeshMinimumSpanningTreeResponse mst =
            await GetAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");
        MeshDeviceRouteResponse route = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{leaf.Id:D}/route");

        Assert.NotEqual(Guid.Empty, gatewayLink.Id);
        Assert.NotEqual(gatewayLink.Id, leafLink.Id);
        Assert.Equal("gateway", gatewayLink.NodeAKey);
        Assert.Equal(SensorKey(relay.Id), gatewayLink.NodeBKey);
        Assert.Equal(before.GraphVersion + 2, graph.GraphVersion);
        Assert.Equal(3, graph.NodeCount);
        Assert.Equal(2, graph.LinkCount);
        Assert.Contains(graph.Links, item => item.Id == gatewayLink.Id);
        Assert.Contains(graph.Links, item => item.Id == leafLink.Id);
        Assert.Equal(graph.GraphVersion, bfs.GraphVersion);
        Assert.Equal(graph.GraphVersion, mst.GraphVersion);
        Assert.Equal(graph.GraphVersion, route.GraphVersion);
        Assert.Equal(2, bfs.ReachableSensorCount);
        Assert.Equal(2, route.Bfs.HopCount);
        Assert.Equal(new[] { SensorKey(leaf.Id), SensorKey(relay.Id), "gateway" },
            route.Bfs.PathToGateway);
        Assert.Equal(2, mst.SelectedEdgeCount);
        Assert.Equal(3.75d, mst.TotalSimulatedEnergyCost);
        Assert.Equal(3.75d, route.Mst.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public async Task DuplicateUnorderedEndpoints_AreRejectedWithoutAdvancingRevision()
    {
        SensorResponse sensor = await RegisterOnlineAsync(0x83, "Duplicate guard");
        await CreateLinkAsync("gateway", SensorKey(sensor.Id), 1d);
        MeshTopologyResponse before = await GetGraphAsync();

        using HttpResponseMessage duplicate = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest(SensorKey(sensor.Id), "gateway", 5d));

        await AssertProblemAsync(duplicate, HttpStatusCode.BadRequest);
        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(1, after.LinkCount);
    }

    [Theory]
    [InlineData("not-a-node", "gateway", 1d)]
    [InlineData("gateway", "sensor/not-a-guid", 1d)]
    [InlineData("gateway", "gateway", 1d)]
    [InlineData("gateway", "sensor/00000000-0000-0000-0000-000000000000", 1d)]
    [InlineData("gateway", "sensor/11111111-1111-1111-1111-111111111111", 1d)]
    [InlineData("gateway", "sensor/11111111-1111-1111-1111-111111111111", 0d)]
    [InlineData("gateway", "sensor/11111111-1111-1111-1111-111111111111", -1d)]
    public async Task InvalidEndpointsOrWeights_AreRejectedWithoutAddingAnyLink(
        string first,
        string second,
        double cost)
    {
        MeshTopologyResponse before = await GetGraphAsync();
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links", new CreateMeshLinkRequest(first, second, cost));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(0, after.LinkCount);
    }

    [Fact]
    public async Task InvalidJsonCost_IsRejectedBeforeItCanTouchTheGraph()
    {
        // Use otherwise VALID endpoints, so the only invalid part is the
        // JSON numeric overflow. No Infinity can enter the authoritative graph.
        SensorResponse sensor = await RegisterOnlineAsync(0x8D, "Overflow guard");
        MeshTopologyResponse before = await GetGraphAsync();
        string body =
            $"{{\"nodeAKey\":\"gateway\",\"nodeBKey\":\"{SensorKey(sensor.Id)}\",\"simulatedEnergyCost\":1e999}}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/topology/links")
        {
            Content = new StringContent(
                body, System.Text.Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(0, after.LinkCount);
    }

    [Fact]
    public async Task OfflineSensor_CanHaveConfiguredLink_ButCannotForwardUntilOnline()
    {
        SensorResponse offline = await RegisterAsync(0x84, "Offline link");
        MeshLinkResponse link = await CreateLinkAsync(
            "gateway", SensorKey(offline.Id), 2d);
        MeshDeviceRouteResponse routeBefore = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{offline.Id:D}/route");
        Assert.Equal("Unavailable", routeBefore.Status);
        Assert.Empty(routeBefore.Bfs.PathToGateway);

        await SetStatusAsync(offline.Id, "Online");
        MeshTopologyResponse graph = await GetGraphAsync();
        MeshDeviceRouteResponse routeAfter = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{offline.Id:D}/route");
        Assert.Contains(graph.Links, item => item.Id == link.Id);
        Assert.Equal("Reachable", routeAfter.Status);
        Assert.Equal(1, routeAfter.Bfs.HopCount);
        Assert.Equal(2d, routeAfter.Mst.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public async Task DecommissionedSensor_CannotReceiveNewSimulationLinks()
    {
        SensorResponse sensor = await RegisterAsync(0x85, "Retired sensor");
        await SetStatusAsync(sensor.Id, "Decommissioned");
        MeshTopologyResponse before = await GetGraphAsync();

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", SensorKey(sensor.Id), 1d));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(0, after.LinkCount);
    }

    [Fact]
    public async Task UpdateWeight_PreservesLinkId_AndRecomputesMinimumTree()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x86, "Cheap path");
        SensorResponse leaf = await RegisterOnlineAsync(0x87, "Competing path");
        MeshLinkResponse first = await CreateLinkAsync(
            "gateway", SensorKey(relay.Id), 1d);
        MeshLinkResponse second = await CreateLinkAsync(
            SensorKey(relay.Id), SensorKey(leaf.Id), 1d);
        MeshLinkResponse direct = await CreateLinkAsync(
            "gateway", SensorKey(leaf.Id), 9d);
        MeshTopologyResponse before = await GetGraphAsync();

        MeshMinimumSpanningTreeResponse initial =
            await GetAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");
        Assert.Equal(2d, initial.TotalSimulatedEnergyCost);

        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{direct.Id:D}/cost",
            new UpdateMeshLinkCostRequest(0.5d));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MeshLinkResponse updated = Assert.IsType<MeshLinkResponse>(
            await response.Content.ReadFromJsonAsync<MeshLinkResponse>());
        Assert.Equal(direct.Id, updated.Id);
        Assert.Equal(direct.NodeAKey, updated.NodeAKey);
        Assert.Equal(direct.NodeBKey, updated.NodeBKey);
        Assert.Equal(0.5d, updated.SimulatedEnergyCost);

        MeshTopologyResponse after = await GetGraphAsync();
        MeshMinimumSpanningTreeResponse revised =
            await GetAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");
        Assert.Equal(before.GraphVersion + 1, after.GraphVersion);
        Assert.Equal(3, after.LinkCount);
        Assert.Equal(1.5d, revised.TotalSimulatedEnergyCost);
        Assert.Equal(2, revised.SelectedEdgeCount);
        Assert.Contains(revised.SelectedEdges, item => item.LinkId == direct.Id);
        Assert.Contains(after.Links, item => item.Id == first.Id);
        Assert.Contains(after.Links, item => item.Id == second.Id);
    }

    [Fact]
    public async Task RepeatedEqualCostPatch_DoesNotAdvanceRevision()
    {
        SensorResponse sensor = await RegisterOnlineAsync(0x88, "No-op guard");
        MeshLinkResponse link = await CreateLinkAsync(
            "gateway", SensorKey(sensor.Id), 2d);
        MeshTopologyResponse before = await GetGraphAsync();

        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{link.Id:D}/cost",
            new UpdateMeshLinkCostRequest(2d));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before.GraphVersion, (await GetGraphAsync()).GraphVersion);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-3d)]
    public async Task InvalidCostUpdate_DoesNotModifyExistingLink(double badCost)
    {
        SensorResponse sensor = await RegisterOnlineAsync(0x89, "Weight guard");
        MeshLinkResponse link = await CreateLinkAsync(
            "gateway", SensorKey(sensor.Id), 2d);
        MeshTopologyResponse before = await GetGraphAsync();

        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{link.Id:D}/cost",
            new UpdateMeshLinkCostRequest(badCost));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        MeshTopologyResponse after = await GetGraphAsync();
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(2d, Assert.Single(after.Links).SimulatedEnergyCost);
    }

    [Fact]
    public async Task DeleteLink_UpdatesReachability_AndSecondDeleteReturns404()
    {
        SensorResponse relay = await RegisterOnlineAsync(0x8A, "Bridge relay");
        SensorResponse leaf = await RegisterOnlineAsync(0x8B, "Downstream");
        await CreateLinkAsync("gateway", SensorKey(relay.Id), 1d);
        MeshLinkResponse bridge = await CreateLinkAsync(
            SensorKey(relay.Id), SensorKey(leaf.Id), 2d);
        MeshTopologyResponse before = await GetGraphAsync();

        using HttpResponseMessage deleted = await _client.DeleteAsync(
            $"/api/topology/links/{bridge.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsStringAsync());

        MeshTopologyResponse after = await GetGraphAsync();
        MeshDeviceRouteResponse leafRoute = await GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{leaf.Id:D}/route");
        Assert.Equal(before.GraphVersion + 1, after.GraphVersion);
        Assert.Equal(1, after.LinkCount);
        Assert.Equal("Disconnected", leafRoute.Status);
        Assert.Empty(leafRoute.Bfs.PathToGateway);
        Assert.Null(leafRoute.Mst.AccumulatedSimulatedEnergyCost);

        using HttpResponseMessage missing = await _client.DeleteAsync(
            $"/api/topology/links/{bridge.Id:D}");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        Assert.Equal(after.GraphVersion, (await GetGraphAsync()).GraphVersion);
    }

    [Fact]
    public async Task InvalidAndMissingLinkIds_Use400Or404ProblemDetails()
    {
        Guid missingId = Guid.NewGuid();
        using HttpResponseMessage badPatch = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{Guid.Empty:D}/cost",
            new UpdateMeshLinkCostRequest(1d));
        await AssertProblemAsync(badPatch, HttpStatusCode.BadRequest);

        using HttpResponseMessage badDelete = await _client.DeleteAsync(
            $"/api/topology/links/{Guid.Empty:D}");
        await AssertProblemAsync(badDelete, HttpStatusCode.BadRequest);

        using HttpResponseMessage missingPatch = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{missingId:D}/cost",
            new UpdateMeshLinkCostRequest(1d));
        await AssertProblemAsync(missingPatch, HttpStatusCode.NotFound);

        using HttpResponseMessage missingDelete = await _client.DeleteAsync(
            $"/api/topology/links/{missingId:D}");
        await AssertProblemAsync(missingDelete, HttpStatusCode.NotFound);
        Assert.Equal(0, (await GetGraphAsync()).GraphVersion);
    }

    [Fact]
    public async Task ProductionWithoutOptIn_RejectsEveryMutation_With403ProblemDetails()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            "Production", simulationOptIn: false);
        using HttpClient client = CreateClient(factory);
        Guid id = Guid.NewGuid();

        using HttpResponseMessage create = await client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", "gateway", 1d));
        await AssertProblemAsync(create, HttpStatusCode.Forbidden);

        using HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/topology/links/{id:D}/cost",
            new UpdateMeshLinkCostRequest(1d));
        await AssertProblemAsync(patch, HttpStatusCode.Forbidden);

        using HttpResponseMessage delete = await client.DeleteAsync(
            $"/api/topology/links/{id:D}");
        await AssertProblemAsync(delete, HttpStatusCode.Forbidden);

        using HttpResponseMessage get = await client.GetAsync("/api/topology");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        MeshTopologyResponse topology = Assert.IsType<MeshTopologyResponse>(
            await get.Content.ReadFromJsonAsync<MeshTopologyResponse>());
        Assert.Equal(0, topology.LinkCount);
        Assert.Equal(0, topology.GraphVersion);
    }

    [Fact]
    public async Task ProductionWithExplicitOptIn_AllowsTrustedSimulationWrites()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            "Production", simulationOptIn: true);
        using HttpClient client = CreateClient(factory);
        SensorResponse sensor = await RegisterAsync(client, 0x8C, "Opted-in demo");

        using HttpResponseMessage create = await client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest("gateway", SensorKey(sensor.Id), 2.5d));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        MeshLinkResponse link = Assert.IsType<MeshLinkResponse>(
            await create.Content.ReadFromJsonAsync<MeshLinkResponse>());
        Assert.Equal(2.5d, link.SimulatedEnergyCost);

        using HttpResponseMessage graphResponse = await client.GetAsync("/api/topology");
        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);
        MeshTopologyResponse graph = Assert.IsType<MeshTopologyResponse>(
            await graphResponse.Content.ReadFromJsonAsync<MeshTopologyResponse>());
        Assert.Contains(graph.Links, item => item.Id == link.Id);
    }

    private async Task<MeshLinkResponse> CreateLinkAsync(
        string first, string second, double cost)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest(first, second, cost));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MeshLinkResponse? link = await response.Content
            .ReadFromJsonAsync<MeshLinkResponse>();
        return Assert.IsType<MeshLinkResponse>(link);
    }

    private Task<MeshTopologyResponse> GetGraphAsync() =>
        GetAsync<MeshTopologyResponse>("/api/topology");

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? value = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(value);
    }

    private Task<SensorResponse> RegisterAsync(byte octet, string name) =>
        RegisterAsync(_client, octet, name);

    private static async Task<SensorResponse> RegisterAsync(
        HttpClient client, byte octet, string name)
    {
        var request = new RegisterSensorRequest(
            name,
            $"AA:BB:CC:DD:FF:{octet:X2}",
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Greenhouse", "Hydroponics", "Rack 1"),
            "Environmental",
            "Float");
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse? sensor = await response.Content
            .ReadFromJsonAsync<SensorResponse>();
        return Assert.IsType<SensorResponse>(sensor);
    }

    private async Task<SensorResponse> RegisterOnlineAsync(byte octet, string name)
    {
        SensorResponse sensor = await RegisterAsync(octet, name);
        await SetStatusAsync(sensor.Id, "Online");
        return sensor;
    }

    private async Task SetStatusAsync(Guid id, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{id:D}/status", new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string SensorKey(Guid id) =>
        MeshNodeKey.ForSensor(id).ToString();

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        bool simulationOptIn) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Topology:Simulation:Enabled"] =
                            simulationOptIn ? "true" : "false"
                    });
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}