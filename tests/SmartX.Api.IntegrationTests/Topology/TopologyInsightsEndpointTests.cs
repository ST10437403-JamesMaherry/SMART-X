using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Sensors;
using SmartX.Contracts.Topology;

namespace SmartX.Api.IntegrationTests.Topology;

/// <summary>
/// P3-10 Commit 2: full HTTP confirmation that first-visit operational
/// warnings derive from actual registered sensors and configured link changes,
/// without requiring search history, learned patterns, or executed commands.
/// Each test runs its own process-local application and graph store.
/// </summary>
public sealed class TopologyInsightsEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TopologyInsightsEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task EmptyRegistry_ExposesAnEmptyReadOnlyProjection()
    {
        TopologyInsightsResponse insights = await ReadInsightsAsync();
        MeshTopologyResponse graph = await GetAsync<MeshTopologyResponse>(
            "/api/topology");

        Assert.Equal(graph.GraphVersion, insights.GraphVersion);
        Assert.Equal(0, insights.RegisteredSensorCount);
        Assert.Equal(0, insights.ReachableSensorCount);
        Assert.Equal(0, insights.DisconnectedSensorCount);
        Assert.Equal(0, insights.UnavailableSensorCount);
        Assert.Empty(insights.Insights);
        Assert.Equal("gateway", Assert.Single(graph.Nodes).Key);
    }

    [Fact]
    public async Task FirstVisitWithoutSearch_ImmediatelyReportsRegisteredDisconnection()
    {
        SensorResponse a = await RegisterOnlineAsync(0xA1, "North relay");
        SensorResponse b = await RegisterOnlineAsync(0xA2, "East relay");
        MeshTopologyResponse before = await GetAsync<MeshTopologyResponse>(
            "/api/topology");

        TopologyInsightsResponse insights = await ReadInsightsAsync();
        TopologyInsightResponse issue = Assert.Single(insights.Insights);

        Assert.Equal(before.GraphVersion, insights.GraphVersion);
        Assert.Equal("GatewayDisconnected", issue.Kind);
        Assert.Equal("Critical", issue.Severity);
        Assert.False(issue.IsLearned);
        Assert.True(issue.IsCurrent);
        Assert.Equal(2, issue.AffectedRegisteredSensorCount);
        Assert.Contains(a.Id, issue.AffectedSensorIds);
        Assert.Contains(b.Id, issue.AffectedSensorIds);
        Assert.Equal("CurrentTopologyBfs", issue.Source);
        Assert.Equal(0, before.LinkCount);

        // Read-only insight GET must not add a simulated link or advance the
        // graph revision; there is no command path through this controller.
        MeshTopologyResponse after = await GetAsync<MeshTopologyResponse>(
            "/api/topology");
        Assert.Equal(before.GraphVersion, after.GraphVersion);
        Assert.Equal(before.LinkCount, after.LinkCount);
    }

    [Fact]
    public async Task RealRelayFailure_Deduplicates_ThenRecoversWithoutLinkDeletion()
    {
        SensorResponse relay = await RegisterOnlineAsync(0xB1, "Primary relay");
        SensorResponse leaf = await RegisterOnlineAsync(0xB2, "Downstream probe");
        await AddLinkAsync("gateway", SensorKey(relay.Id));
        await AddLinkAsync(SensorKey(relay.Id), SensorKey(leaf.Id));

        Assert.Empty((await ReadInsightsAsync()).Insights);

        await SetStatusAsync(relay.Id, "Offline");
        MeshTopologyResponse failedGraph = await GetAsync<MeshTopologyResponse>(
            "/api/topology");
        TopologyInsightsResponse failure = await ReadInsightsAsync();
        Assert.Equal(failedGraph.GraphVersion, failure.GraphVersion);
        Assert.Equal(2, failedGraph.LinkCount);
        Assert.Equal(1, failure.DisconnectedSensorCount);
        Assert.Equal(1, failure.UnavailableSensorCount);
        Assert.Equal(2, failure.Insights.Count);
        TopologyInsightResponse disconnection = Assert.Single(failure.Insights,
            item => item.Kind == "GatewayDisconnected");
        Assert.Equal(leaf.Id, Assert.Single(disconnection.AffectedSensorIds));
        TopologyInsightResponse offline = Assert.Single(failure.Insights,
            item => item.Kind == "NonForwardingDevices");
        Assert.Equal(relay.Id, Assert.Single(offline.AffectedSensorIds));

        TopologyInsightsResponse again = await ReadInsightsAsync();
        Assert.Equal(failure.Insights.Count, again.Insights.Count);
        Assert.Equal(failure.ProjectionComputedAtUtc,
            again.ProjectionComputedAtUtc);
        Assert.Equal(disconnection.FirstObservedAtUtc,
            again.Insights.Single(item => item.Kind == "GatewayDisconnected")
                .FirstObservedAtUtc);

        await SetStatusAsync(relay.Id, "Online");
        TopologyInsightsResponse recovered = await ReadInsightsAsync();
        TopologyInsightResponse restored = Assert.Single(recovered.Insights);
        Assert.Equal("GatewayConnectivityRestored", restored.Kind);
        Assert.False(restored.IsLearned);
        Assert.False(restored.IsCurrent);
        Assert.Equal(leaf.Id, Assert.Single(restored.AffectedSensorIds));
        Assert.True(restored.ExpiresAtUtc > recovered.EvaluatedAtUtc);
        Assert.Equal(2, recovered.ReachableSensorCount);
        Assert.Equal(0, recovered.DisconnectedSensorCount);

        MeshTopologyResponse recoveredGraph = await GetAsync<MeshTopologyResponse>(
            "/api/topology");
        Assert.Equal(2, recoveredGraph.LinkCount);
        Assert.Equal(recoveredGraph.GraphVersion, recovered.GraphVersion);
    }

    private async Task<TopologyInsightsResponse> ReadInsightsAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            "/api/topology/insights");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(true, response.Headers.CacheControl?.NoStore);
        TopologyInsightsResponse? result = await response.Content
            .ReadFromJsonAsync<TopologyInsightsResponse>();
        return Assert.IsType<TopologyInsightsResponse>(result);
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? result = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(result);
    }

    private async Task<SensorResponse> RegisterOnlineAsync(byte octet, string name)
    {
        var request = new RegisterSensorRequest(
            name,
            $"AA:BB:CC:DD:EE:{octet:X2}",
            "MacAddress",
            new SensorDeploymentLocationRequest("Greenhouse", "Hydroponics", "A1"),
            "Environmental",
            "Float");

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse sensor = Assert.IsType<SensorResponse>(
            await response.Content.ReadFromJsonAsync<SensorResponse>());
        await SetStatusAsync(sensor.Id, "Online");
        return sensor;
    }

    private async Task SetStatusAsync(Guid id, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{id:D}/status",
            new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AddLinkAsync(string first, string second)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest(first, second, 1d));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string SensorKey(Guid id) => $"sensor/{id:D}";

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}