using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Sensors;
using SmartX.Contracts.Topology;

namespace SmartX.Api.IntegrationTests.Topology;

/// <summary>
/// P3-07 Commit 4: reproducible greenhouse demonstration through the REAL
/// ASP.NET Core HTTP pipeline. No test inserts links into IMeshTopologyStore,
/// sets sensor health through a repository, or replaces the BFS/Prim services.
/// Each test has its own WebApplicationFactory and isolated in-memory stores.
///
/// The logical network has a redundant gateway loop:
///
///   gateway --1-- primary --1-- moisture --1-- repeater --1-- backup --5-- gateway
///                   |                                             |
///                  0.5                                           1.25
///                   |                                             |
///               vulnerable                                     nutrient
///
/// Seven configured UNDIRECTED links, six registered sensors. All costs are
/// relative, simulated energy weights; this is not a physical radio network.
/// </summary>
public sealed class MeshGreenhouseEndToEndScenarioTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public MeshGreenhouseEndToEndScenarioTests()
    {
        // Development enables ONLY the existing logical simulation writes.
        // Other test suites already prove that Production blocks these writes.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseEnvironment("Development"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Greenhouse_UsesRealHttpRegistrations_AndSelectsMinimumCostBackbone()
    {
        Greenhouse network = await SeedGreenhouseAsync();
        MeshTopologyResponse graph = await GetTopologyAsync();
        MeshReachabilityResponse bfs = await GetReachabilityAsync();
        MeshMinimumSpanningTreeResponse mst = await GetMstAsync();
        MeshDeviceRouteResponse moisture = await GetRouteAsync(network.Moisture.Id);
        MeshDeviceRouteResponse nutrient = await GetRouteAsync(network.Nutrient.Id);

        Assert.Equal(7, graph.NodeCount); // Gateway plus six ACTUAL registrations.
        Assert.Equal(7, graph.LinkCount); // Includes unused backup gateway link.
        Assert.Equal(6, bfs.RegisteredSensorCount);
        Assert.Equal(6, bfs.ReachableSensorCount);
        Assert.Equal(0, bfs.DisconnectedSensorCount);
        Assert.Equal(0, bfs.UnavailableSensorCount);
        Assert.Equal(6, mst.SelectedEdgeCount);
        Assert.Equal(5.75d, mst.TotalSimulatedEnergyCost);
        Assert.Equal(graph.GraphVersion, bfs.GraphVersion);
        Assert.Equal(graph.GraphVersion, mst.GraphVersion);
        Assert.Equal(graph.GraphVersion, moisture.GraphVersion);
        Assert.Equal(graph.GraphVersion, nutrient.GraphVersion);

        // The MST selects every cheap branch and excludes the costly GW-backup
        // link. The configured mesh still exposes that backup as a real edge.
        Assert.DoesNotContain(mst.SelectedEdges,
            edge => edge.LinkId == network.GatewayBackup.Id);
        Assert.Contains(graph.Links,
            link => link.Id == network.GatewayBackup.Id &&
                link.SimulatedEnergyCost == 5d);
        Assert.Equal(6, mst.SelectedEdges.Select(edge => edge.LinkId)
            .Distinct().Count());

        // No client-generated placeholder sensor names, IDs, or health states.
        MeshVertexResponse relay = Assert.Single(graph.Nodes,
            node => node.SensorProfileId == network.Primary.Id);
        Assert.Equal("Primary irrigation relay", relay.Name);
        Assert.Equal(network.Primary.DeviceIdentifier, relay.DeviceIdentifier);
        Assert.Equal("Actuator", relay.Category);
        Assert.Equal("Online", relay.Status);
        Assert.True(relay.IsForwardingEligible);
        Assert.Contains(graph.Nodes,
            node => node.SensorProfileId == network.Vulnerable.Id &&
                node.Name == "Single-bridge moisture probe");

        Assert.Equal("Reachable", moisture.Status);
        Assert.Equal(new[] { Key(network.Moisture.Id),
            Key(network.Primary.Id), "gateway" }, moisture.Bfs.PathToGateway);
        Assert.Equal(moisture.Bfs.PathToGateway.ToArray(),
            moisture.Mst.PathToGateway.ToArray());
        Assert.Equal(2, moisture.Bfs.HopCount);
        Assert.Equal(2d, moisture.Mst.AccumulatedSimulatedEnergyCost);

        // BFS uses the two-hop direct configured link through the backup.
        // Prim uses a five-hop route in its CHEAPER total backbone.
        Assert.Equal(new[] { Key(network.Nutrient.Id),
            Key(network.Backup.Id), "gateway" }, nutrient.Bfs.PathToGateway);
        Assert.Equal(new[] { Key(network.Nutrient.Id),
            Key(network.Backup.Id), Key(network.Repeater.Id),
            Key(network.Moisture.Id), Key(network.Primary.Id), "gateway" },
            nutrient.Mst.PathToGateway);
        Assert.Equal(2, nutrient.Bfs.HopCount);
        Assert.Equal(5, nutrient.Mst.HopCount);
        Assert.Equal(5.25d, nutrient.Mst.AccumulatedSimulatedEnergyCost);

        // GET requests must not mutate the graph or invalidate this revision.
        Assert.Equal(graph.GraphVersion, (await GetTopologyAsync()).GraphVersion);
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Fault")]
    [InlineData("Maintenance")]
    public async Task PrimaryRelayFailure_UsesBackup_AndReportsVulnerableLeaf(
        string failedStatus)
    {
        Greenhouse network = await SeedGreenhouseAsync();
        MeshTopologyResponse before = await GetTopologyAsync();
        MeshDeviceRouteResponse oldMoisture = await GetRouteAsync(network.Moisture.Id);

        await SetStatusAsync(network.Primary.Id, failedStatus);

        MeshTopologyResponse graph = await GetTopologyAsync();
        MeshReachabilityResponse bfs = await GetReachabilityAsync();
        MeshMinimumSpanningTreeResponse mst = await GetMstAsync();
        MeshDeviceRouteResponse moisture = await GetRouteAsync(network.Moisture.Id);
        MeshDeviceRouteResponse relay = await GetRouteAsync(network.Primary.Id);
        MeshDeviceRouteResponse vulnerable = await GetRouteAsync(network.Vulnerable.Id);

        Assert.True(graph.GraphVersion > before.GraphVersion);
        Assert.Equal(7, graph.LinkCount); // Failure does NOT delete configured links.
        Assert.Equal(graph.GraphVersion, bfs.GraphVersion);
        Assert.Equal(graph.GraphVersion, mst.GraphVersion);
        Assert.Equal(graph.GraphVersion, moisture.GraphVersion);
        Assert.Equal(4, bfs.ReachableSensorCount); // Backup, repeater, moisture, nutrient.
        Assert.Equal(1, bfs.DisconnectedSensorCount); // Vulnerable probe.
        Assert.Equal(1, bfs.UnavailableSensorCount); // Primary relay.
        Assert.Contains(bfs.UnavailableSensorKeys, key =>
            key == Key(network.Primary.Id));
        Assert.Contains(bfs.DisconnectedComponents, component =>
            component.SensorKeys.Count == 1 &&
            component.SensorKeys[0] == Key(network.Vulnerable.Id));
        Assert.Equal(4, mst.SelectedEdgeCount);
        Assert.Equal(8.25d, mst.TotalSimulatedEnergyCost);
        Assert.Contains(mst.SelectedEdges,
            edge => edge.LinkId == network.GatewayBackup.Id);

        Assert.Equal("Reachable", moisture.Status);
        Assert.Equal(new[] { Key(network.Moisture.Id),
            Key(network.Repeater.Id), Key(network.Backup.Id), "gateway" },
            moisture.Bfs.PathToGateway);
        Assert.Equal(moisture.Bfs.PathToGateway.ToArray(),
            moisture.Mst.PathToGateway.ToArray());
        Assert.Equal(3, moisture.Bfs.HopCount);
        Assert.Equal(7d, moisture.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal("Unavailable", relay.Status);
        Assert.Empty(relay.Bfs.PathToGateway);
        Assert.Equal("Disconnected", vulnerable.Status);
        Assert.Empty(vulnerable.Bfs.PathToGateway);
        Assert.Empty(vulnerable.Mst.PathToGateway);
        Assert.Null(vulnerable.Mst.AccumulatedSimulatedEnergyCost);

        // Previously returned transport data is a defensively mapped snapshot.
        Assert.Equal(before.GraphVersion, oldMoisture.GraphVersion);
        Assert.Equal(2, oldMoisture.Bfs.HopCount);
        Assert.Equal(2d, oldMoisture.Mst.AccumulatedSimulatedEnergyCost);
    }

    [Fact]
    public async Task RecoveredRelay_RestoresOriginalBackboneWithoutRecreatingEdges()
    {
        Greenhouse network = await SeedGreenhouseAsync();
        MeshTopologyResponse original = await GetTopologyAsync();

        await SetStatusAsync(network.Primary.Id, "Offline");
        MeshMinimumSpanningTreeResponse failed = await GetMstAsync();
        Assert.Equal(8.25d, failed.TotalSimulatedEnergyCost);

        await SetStatusAsync(network.Primary.Id, "Online");
        MeshTopologyResponse restored = await GetTopologyAsync();
        MeshMinimumSpanningTreeResponse recovered = await GetMstAsync();
        MeshDeviceRouteResponse vulnerable = await GetRouteAsync(network.Vulnerable.Id);
        MeshDeviceRouteResponse moisture = await GetRouteAsync(network.Moisture.Id);

        Assert.True(restored.GraphVersion > original.GraphVersion);
        Assert.Equal(original.LinkCount, restored.LinkCount);
        Assert.Equal(original.Links.Select(link => link.Id).OrderBy(id => id),
            restored.Links.Select(link => link.Id).OrderBy(id => id));
        Assert.Equal(5.75d, recovered.TotalSimulatedEnergyCost);
        Assert.Equal(6, recovered.SelectedEdgeCount);
        Assert.DoesNotContain(recovered.SelectedEdges,
            edge => edge.LinkId == network.GatewayBackup.Id);
        Assert.Equal("Reachable", vulnerable.Status);
        Assert.Equal(new[] { Key(network.Vulnerable.Id),
            Key(network.Primary.Id), "gateway" }, vulnerable.Mst.PathToGateway);
        Assert.Equal(1.5d, vulnerable.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(new[] { Key(network.Moisture.Id),
            Key(network.Primary.Id), "gateway" }, moisture.Mst.PathToGateway);
        Assert.Equal(restored.GraphVersion, recovered.GraphVersion);
        Assert.Equal(restored.GraphVersion, moisture.GraphVersion);
    }

    [Fact]
    public async Task RemovingOnlyBackup_ThenFailingPrimary_ProducesTwoDisconnectedIslands()
    {
        Greenhouse network = await SeedGreenhouseAsync();
        MeshTopologyResponse initial = await GetTopologyAsync();

        await DeleteLinkAsync(network.GatewayBackup.Id);
        MeshTopologyResponse afterCut = await GetTopologyAsync();
        Assert.Equal(initial.GraphVersion + 1, afterCut.GraphVersion);
        Assert.Equal(6, afterCut.LinkCount);
        // The backup edge was NOT in the old MST, so deleting it initially
        // changes resilience, not the selected minimum-cost backbone.
        Assert.Equal(5.75d, (await GetMstAsync()).TotalSimulatedEnergyCost);

        await SetStatusAsync(network.Primary.Id, "Offline");
        MeshTopologyResponse isolated = await GetTopologyAsync();
        MeshReachabilityResponse bfs = await GetReachabilityAsync();
        MeshMinimumSpanningTreeResponse mst = await GetMstAsync();
        MeshDeviceRouteResponse moisture = await GetRouteAsync(network.Moisture.Id);
        MeshDeviceRouteResponse vulnerable = await GetRouteAsync(network.Vulnerable.Id);

        Assert.True(isolated.GraphVersion > afterCut.GraphVersion);
        Assert.Equal(0, bfs.ReachableSensorCount);
        Assert.Equal(5, bfs.DisconnectedSensorCount);
        Assert.Equal(1, bfs.UnavailableSensorCount);
        Assert.Equal(2, bfs.DisconnectedComponents.Count);
        Assert.Contains(bfs.DisconnectedComponents, component =>
            component.SensorKeys.Count == 1 &&
            component.SensorKeys[0] == Key(network.Vulnerable.Id));
        string[] mainIsland =
        [
            Key(network.Backup.Id), Key(network.Repeater.Id),
            Key(network.Moisture.Id), Key(network.Nutrient.Id)
        ];
        Assert.Contains(bfs.DisconnectedComponents, component =>
            component.SensorKeys.Count == 4 &&
            component.SensorKeys.OrderBy(key => key)
                .SequenceEqual(mainIsland.OrderBy(key => key)));
        Assert.Equal(0, mst.SelectedEdgeCount);
        Assert.Equal(0d, mst.TotalSimulatedEnergyCost);
        Assert.Equal("Disconnected", moisture.Status);
        Assert.Equal("Disconnected", vulnerable.Status);
        Assert.Empty(moisture.Bfs.PathToGateway);
        Assert.Empty(moisture.Mst.SelectedLinkIdsToGateway);
        Assert.Null(moisture.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(isolated.GraphVersion, mst.GraphVersion);
    }

    [Fact]
    public async Task ReaddingBackupLink_RecoversOnlySensorsWithValidForwardingPaths()
    {
        Greenhouse network = await SeedGreenhouseAsync();
        await DeleteLinkAsync(network.GatewayBackup.Id);
        await SetStatusAsync(network.Primary.Id, "Offline");
        MeshTopologyResponse withoutBackup = await GetTopologyAsync();
        Assert.Equal(0, (await GetReachabilityAsync()).ReachableSensorCount);

        // The HTTP POST assigns a NEW link ID. It cannot reactivate the
        // unavailable relay or invent a link to the single-bridge leaf.
        MeshLinkResponse restoredBackup = await CreateLinkAsync(
            "gateway", Key(network.Backup.Id), 5d);
        MeshTopologyResponse recovered = await GetTopologyAsync();
        MeshReachabilityResponse bfs = await GetReachabilityAsync();
        MeshMinimumSpanningTreeResponse mst = await GetMstAsync();
        MeshDeviceRouteResponse moisture = await GetRouteAsync(network.Moisture.Id);
        MeshDeviceRouteResponse vulnerable = await GetRouteAsync(network.Vulnerable.Id);

        Assert.NotEqual(network.GatewayBackup.Id, restoredBackup.Id);
        Assert.Equal(withoutBackup.GraphVersion + 1, recovered.GraphVersion);
        Assert.Equal(7, recovered.LinkCount);
        Assert.Equal(4, bfs.ReachableSensorCount);
        Assert.Equal(1, bfs.DisconnectedSensorCount);
        Assert.Equal(1, bfs.UnavailableSensorCount);
        Assert.Equal(8.25d, mst.TotalSimulatedEnergyCost);
        Assert.Contains(mst.SelectedEdges,
            edge => edge.LinkId == restoredBackup.Id);
        Assert.Equal("Reachable", moisture.Status);
        Assert.Equal(new[] { Key(network.Moisture.Id),
            Key(network.Repeater.Id), Key(network.Backup.Id), "gateway" },
            moisture.Mst.PathToGateway);
        Assert.Equal("Disconnected", vulnerable.Status);
        Assert.Empty(vulnerable.Mst.PathToGateway);
    }

    [Fact]
    public async Task ChangingBackupCost_RecomputesMstButNotBfsHopCount()
    {
        Greenhouse network = await SeedGreenhouseAsync();
        MeshTopologyResponse before = await GetTopologyAsync();
        MeshDeviceRouteResponse nutrientBefore = await GetRouteAsync(network.Nutrient.Id);
        Assert.Equal(5, nutrientBefore.Mst.HopCount);
        Assert.Equal(2, nutrientBefore.Bfs.HopCount);

        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/topology/links/{network.GatewayBackup.Id:D}/cost",
            new UpdateMeshLinkCostRequest(0.25d));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MeshLinkResponse updated = Assert.IsType<MeshLinkResponse>(
            await response.Content.ReadFromJsonAsync<MeshLinkResponse>());
        Assert.Equal(network.GatewayBackup.Id, updated.Id);
        Assert.Equal(0.25d, updated.SimulatedEnergyCost);

        MeshTopologyResponse after = await GetTopologyAsync();
        MeshMinimumSpanningTreeResponse mst = await GetMstAsync();
        MeshDeviceRouteResponse nutrientAfter = await GetRouteAsync(network.Nutrient.Id);
        Assert.Equal(before.GraphVersion + 1, after.GraphVersion);
        Assert.Equal(5d, mst.TotalSimulatedEnergyCost);
        Assert.Contains(mst.SelectedEdges,
            edge => edge.LinkId == network.GatewayBackup.Id);
        Assert.Equal(nutrientBefore.Bfs.PathToGateway.ToArray(),
            nutrientAfter.Bfs.PathToGateway.ToArray());
        Assert.Equal(2, nutrientAfter.Bfs.HopCount);
        Assert.Equal(new[] { Key(network.Nutrient.Id),
            Key(network.Backup.Id), "gateway" }, nutrientAfter.Mst.PathToGateway);
        Assert.Equal(1.5d, nutrientAfter.Mst.AccumulatedSimulatedEnergyCost);
        Assert.Equal(after.GraphVersion, mst.GraphVersion);
        Assert.Equal(after.GraphVersion, nutrientAfter.GraphVersion);
    }

    /// <summary>
    /// Creates the complete graph only through documented HTTP writes.
    /// Registrations yield new GUIDs; key ordering is never assumed.
    /// The weighted MST is unique as an EDGE SET in the base scenario.
    /// </summary>
    private async Task<Greenhouse> SeedGreenhouseAsync()
    {
        SensorResponse primary = await RegisterOnlineAsync(
            0xA1, "Primary irrigation relay", "Actuator");
        SensorResponse backup = await RegisterOnlineAsync(
            0xA2, "Backup irrigation relay", "Actuator");
        SensorResponse moisture = await RegisterOnlineAsync(
            0xA3, "West-zone moisture probe", "Environmental");
        SensorResponse repeater = await RegisterOnlineAsync(
            0xA4, "Central greenhouse repeater", "Actuator");
        SensorResponse nutrient = await RegisterOnlineAsync(
            0xA5, "East-zone nutrient probe", "Environmental");
        SensorResponse vulnerable = await RegisterOnlineAsync(
            0xA6, "Single-bridge moisture probe", "Environmental");

        await CreateLinkAsync("gateway", Key(primary.Id), 1d);
        MeshLinkResponse gatewayBackup = await CreateLinkAsync(
            "gateway", Key(backup.Id), 5d);
        await CreateLinkAsync(Key(primary.Id), Key(moisture.Id), 1d);
        await CreateLinkAsync(Key(moisture.Id), Key(repeater.Id), 1d);
        await CreateLinkAsync(Key(repeater.Id), Key(backup.Id), 1d);
        await CreateLinkAsync(Key(backup.Id), Key(nutrient.Id), 1.25d);
        await CreateLinkAsync(Key(primary.Id), Key(vulnerable.Id), 0.5d);

        return new Greenhouse(primary, backup, moisture, repeater,
            nutrient, vulnerable, gatewayBackup);
    }

    private async Task<SensorResponse> RegisterOnlineAsync(
        byte suffix, string name, string category)
    {
        var request = new RegisterSensorRequest(
            name,
            $"AA:BB:CC:EE:07:{suffix:X2}",
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Greenhouse", "Hydroponics", $"Zone-{suffix:X2}"),
            category,
            "Float");
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse sensor = Assert.IsType<SensorResponse>(
            await response.Content.ReadFromJsonAsync<SensorResponse>());
        await SetStatusAsync(sensor.Id, "Online");
        return sensor;
    }

    private async Task SetStatusAsync(Guid sensorId, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{sensorId:D}/status",
            new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<MeshLinkResponse> CreateLinkAsync(
        string first, string second, double cost)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/topology/links",
            new CreateMeshLinkRequest(first, second, cost));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<MeshLinkResponse>(
            await response.Content.ReadFromJsonAsync<MeshLinkResponse>());
    }

    private async Task DeleteLinkAsync(Guid linkId)
    {
        using HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/topology/links/{linkId:D}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<MeshTopologyResponse> GetTopologyAsync() =>
        GetAsync<MeshTopologyResponse>("/api/topology");

    private Task<MeshReachabilityResponse> GetReachabilityAsync() =>
        GetAsync<MeshReachabilityResponse>("/api/topology/reachability");

    private Task<MeshMinimumSpanningTreeResponse> GetMstAsync() =>
        GetAsync<MeshMinimumSpanningTreeResponse>("/api/topology/mst");

    private Task<MeshDeviceRouteResponse> GetRouteAsync(Guid sensorId) =>
        GetAsync<MeshDeviceRouteResponse>(
            $"/api/topology/devices/{sensorId:D}/route");

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static string Key(Guid id) => $"sensor/{id:D}";

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed record Greenhouse(
        SensorResponse Primary,
        SensorResponse Backup,
        SensorResponse Moisture,
        SensorResponse Repeater,
        SensorResponse Nutrient,
        SensorResponse Vulnerable,
        MeshLinkResponse GatewayBackup);
}