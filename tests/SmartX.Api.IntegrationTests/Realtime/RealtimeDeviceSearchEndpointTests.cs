using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Exercises the real HTTP route, canonical Dictionary lookup, native sensor
/// updates and HashSet-backed issue lifecycle. Tests use isolated API hosts.
/// </summary>
public sealed class RealtimeDeviceSearchEndpointTests : IDisposable
{
    private static readonly string TestEncryptionKey = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    private readonly string _storagePath = Path.Combine(
        Path.GetTempPath(), "smartx-p208-device-search", Guid.NewGuid().ToString("N"));

    private readonly DeviceSearchApiFactory _factory;
    private readonly HttpClient _client;

    public RealtimeDeviceSearchEndpointTests()
    {
        Directory.CreateDirectory(_storagePath);
        _factory = new DeviceSearchApiFactory(_storagePath, TestEncryptionKey);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task Search_EmptyRegistry_ReturnsBoundedEmptyPage()
    {
        RealtimeDeviceSearchResponse result = await SearchAsync();

        Assert.Equal(0, result.TotalMatches);
        Assert.Empty(result.Devices);
        Assert.Equal(25, result.Limit);
        Assert.Equal(0, result.Offset);
        Assert.False(result.HasMore);
        Assert.NotEqual(default, result.GeneratedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.GeneratedAtUtc.Offset);
    }

    [Fact]
    public async Task Search_ExactMac_UsesCanonicalLookupAndCombinesAdditionalFilters()
    {
        SensorResponse target = await RegisterAsync(
            "Hydroponic Pump A", "AA:BB:CC:DD:EE:91", "MacAddress",
            "PowerConsumption", "Integer", "North Facility");
        await RegisterAsync(
            "Hydroponic Pump B", "AA:BB:CC:DD:EE:92", "MacAddress",
            "PowerConsumption", "Integer", "South Facility");

        RealtimeDeviceSearchResponse result = await SearchAsync(
            "?identifierKind=macaddress&deviceIdentifier=aabbccddee91" +
            "&text=pump&category=powerconsumption&status=offline&limit=1");

        RealtimeDeviceSearchItemResponse item = Assert.Single(result.Devices);
        Assert.Equal(target.Id, item.Device.Id);
        Assert.Equal("AA:BB:CC:DD:EE:91", item.Device.DeviceIdentifier);
        Assert.Equal("AA:BB:CC:DD:EE:91", result.AppliedFilters.DeviceIdentifier);
        Assert.Equal("MacAddress", result.AppliedFilters.IdentifierKind);
        Assert.Equal("PowerConsumption", result.AppliedFilters.Category);
        Assert.Equal("Offline", result.AppliedFilters.Status);
        Assert.Equal(1, result.TotalMatches);
        Assert.False(result.HasMore);

        RealtimeDeviceSearchResponse noMatches = await SearchAsync(
            "?identifierKind=MacAddress&deviceIdentifier=aa-bb-cc-dd-ee-91" +
            "&category=Actuator");
        Assert.Empty(noMatches.Devices);
        Assert.Equal(0, noMatches.TotalMatches);
    }

    [Fact]
    public async Task Search_CustomIdentifier_UsesOrdinalCaseAndNoMatchReturnsEmpty()
    {
        SensorResponse sensor = await RegisterAsync(
            "Control Node", "Node_Alpha", "UniqueIdentifier",
            "Environmental", "Float", "Test Facility");

        RealtimeDeviceSearchResponse exact = await SearchAsync(
            "?identifierKind=UniqueIdentifier&deviceIdentifier=Node_Alpha");
        Assert.Equal(sensor.Id, Assert.Single(exact.Devices).Device.Id);

        RealtimeDeviceSearchResponse caseMismatch = await SearchAsync(
            "?identifierKind=UniqueIdentifier&deviceIdentifier=node_alpha");
        Assert.Empty(caseMismatch.Devices);

        // Preserve the P2-02 legacy 404 contract on the pre-existing route.
        HttpResponseMessage legacy = await _client.GetAsync(
            "/api/realtime/devices?identifierKind=UniqueIdentifier" +
            "&deviceIdentifier=node_alpha");
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [Fact]
    public async Task Search_TextCategoryAndStatus_IntersectsFilters()
    {
        SensorResponse chosen = await RegisterAsync(
            "North Temperature", "node-north-temp", "UniqueIdentifier",
            "Environmental", "Float", "North Lab");
        await RegisterAsync(
            "North Valve", "node-north-valve", "UniqueIdentifier",
            "Actuator", "Boolean", "North Lab");
        await RegisterAsync(
            "South Temperature", "node-south-temp", "UniqueIdentifier",
            "Environmental", "Float", "South Lab");

        await ChangeStatusAsync(chosen.Id, "Online");

        RealtimeDeviceSearchResponse combined = await SearchAsync(
            "?text=north&category=Environmental&status=ONLINE");

        Assert.Equal(chosen.Id, Assert.Single(combined.Devices).Device.Id);
        Assert.Equal(1, combined.TotalMatches);
        Assert.Equal("north", combined.AppliedFilters.Text);

        // Name and deployment text both participate in simple substring search.
        RealtimeDeviceSearchResponse location = await SearchAsync("?text=South%20Lab");
        Assert.Single(location.Devices);

        RealtimeDeviceSearchResponse empty = await SearchAsync(
            "?text=does-not-exist&category=Actuator");
        Assert.Empty(empty.Devices);
        Assert.Equal(0, empty.TotalMatches);
    }

    [Fact]
    public async Task Search_LimitAndOffset_PreserveStableRegistryOrder()
    {
        for (int i = 0; i < 5; i++)
        {
            await RegisterAsync(
                $"Paged Device {i}", $"node-page-{i}", "UniqueIdentifier",
                "Environmental", "Float", "Paging Lab");
        }

        RealtimeDeviceSearchResponse first = await SearchAsync("?limit=2&offset=0");
        RealtimeDeviceSearchResponse second = await SearchAsync("?limit=2&offset=2");
        RealtimeDeviceSearchResponse third = await SearchAsync("?limit=2&offset=4");
        RealtimeDeviceSearchResponse beyond = await SearchAsync("?limit=2&offset=30");

        Assert.Equal(5, first.TotalMatches);
        Assert.Equal(5, second.TotalMatches);
        Assert.Equal(5, third.TotalMatches);
        Assert.Equal(2, first.Devices.Count);
        Assert.Equal(2, second.Devices.Count);
        Assert.Single(third.Devices);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        Assert.False(beyond.HasMore);
        Assert.Empty(beyond.Devices);
        Assert.Equal(5, beyond.TotalMatches);

        RealtimeDeviceResponse[] legacy =
            await _client.GetFromJsonAsync<RealtimeDeviceResponse[]>("/api/realtime/devices") ?? [];

        Guid[] expected = legacy.Select(device => device.Id).ToArray();
        Guid[] actual = first.Devices.Concat(second.Devices).Concat(third.Devices)
            .Select(result => result.Device.Id)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Search_ActiveConditionFilter_TracksAcknowledgementAndRecovery()
    {
        SensorResponse sensor = await RegisterAsync(
            "Edge Sensor", "node-disconnect-1", "UniqueIdentifier",
            "Environmental", "Float", "Edge Lab");
        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Offline");

        string filter = "?conditionType=SensorDisconnected&hasActiveCondition=true";
        RealtimeDeviceSearchResponse active = await SearchAsync(filter);
        RealtimeDeviceSearchItemResponse item = Assert.Single(active.Devices);

        Assert.Equal(sensor.Id, item.Device.Id);
        Assert.True(item.ActiveConditionCount >= 1);
        Assert.NotNull(item.HighestPriorityIssueId);

        DashboardSnapshotResponse dashboard =
            await _client.GetFromJsonAsync<DashboardSnapshotResponse>("/api/dashboard/issues") ??
            throw new InvalidOperationException("Dashboard response missing.");

        DashboardIssueResponse disconnect = Assert.Single(dashboard.Issues,
            issue => issue.SensorProfileId == sensor.Id && issue.Type == "SensorDisconnected");

        HttpResponseMessage acknowledgement = await _client.PostAsync(
            $"/api/dashboard/issues/{disconnect.Id}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, acknowledgement.StatusCode);

        // An acknowledged unresolved issue still belongs to the HashSet.
        RealtimeDeviceSearchResponse acknowledged = await SearchAsync(filter);
        Assert.Equal(sensor.Id, Assert.Single(acknowledged.Devices).Device.Id);

        await ChangeStatusAsync(sensor.Id, "Online");

        RealtimeDeviceSearchResponse recovered = await SearchAsync(filter);
        Assert.Empty(recovered.Devices);
        Assert.Equal(0, recovered.TotalMatches);

    }

    [Fact]
    public async Task Search_ExplicitNoActiveFilter_DistinguishesFalseFromMissing()
    {
        SensorResponse sensor = await RegisterAsync(
            "Untouched Registry Device", "node-unobserved", "UniqueIdentifier",
            "Environmental", "Float", "Empty Conditions Lab");

        // Sensor registration does not generate configuration issues until
        // the original dashboard performs its explicit reconciliation.
        RealtimeDeviceSearchResponse result =
            await SearchAsync("?hasActiveCondition=false");

        Assert.Equal(sensor.Id, Assert.Single(result.Devices).Device.Id);
        Assert.Equal(false, result.AppliedFilters.HasActiveCondition);
        Assert.Equal(0, result.Devices[0].ActiveConditionCount);
    }

    [Theory]
    [InlineData("?identifierKind=MacAddress")]
    [InlineData("?deviceIdentifier=AA%3ABB%3ACC%3ADD%3AEE%3A11")]
    [InlineData("?identifierKind=MacAddress&deviceIdentifier=not-a-mac")]
    [InlineData("?category=Unknown")]
    [InlineData("?category=1")]
    [InlineData("?status=Destroyed")]
    [InlineData("?status=2")]
    [InlineData("?conditionType=Nope")]
    [InlineData("?hasActiveCondition=maybe")]
    [InlineData("?hasActiveCondition=false&conditionType=TelemetrySpike")]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?limit=1.5")]
    [InlineData("?offset=-1")]
    [InlineData("?offset=100001")]
    [InlineData("?text=")]
    [InlineData("?status=")]
    [InlineData("?limit=")]
    [InlineData("?limit=2&limit=3")]
    public async Task Search_InvalidFilters_UseProblemDetailsWithoutSideEffects(string query)
    {
        HttpResponseMessage response =
            await _client.GetAsync($"/api/realtime/devices/search{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Invalid device search query", problem.Title);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    [Fact]
    public async Task Search_RepeatedRefreshes_DoNotCreateOrIncrementIssueEpisodes()
    {
        SensorResponse sensor = await RegisterAsync(
            "Refresh Device", "node-refresh", "UniqueIdentifier",
            "Environmental", "Float", "Refresh Lab");
        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Offline");

        string url = "?identifierKind=UniqueIdentifier&deviceIdentifier=node-refresh" +
                     "&hasActiveCondition=true&conditionType=SensorDisconnected";

        RealtimeDeviceSearchResponse first = await SearchAsync(url);
        RealtimeDeviceSearchResponse second = await SearchAsync(url);
        RealtimeDeviceSearchResponse third = await SearchAsync(url);

        RealtimeDeviceSearchItemResponse firstItem = Assert.Single(first.Devices);
        Assert.Equal(firstItem.HighestPriorityIssueId,
            Assert.Single(second.Devices).HighestPriorityIssueId);
        Assert.Equal(firstItem.HighestPriorityIssueId,
            Assert.Single(third.Devices).HighestPriorityIssueId);

        DashboardSnapshotResponse dashboard =
            await _client.GetFromJsonAsync<DashboardSnapshotResponse>("/api/dashboard/issues") ??
            throw new InvalidOperationException("Dashboard response missing.");

        DashboardIssueResponse issue = Assert.Single(dashboard.Issues,
            candidate => candidate.SensorProfileId == sensor.Id &&
                         candidate.Type == "SensorDisconnected");

        Assert.Equal(1, issue.OccurrenceCount);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();

        // The tests never share storage and clean up their own attachment data.
        if (Directory.Exists(_storagePath))
        {
            Directory.Delete(_storagePath, recursive: true);
        }
    }

    private async Task<RealtimeDeviceSearchResponse> SearchAsync(string query = "")
    {
        HttpResponseMessage response =
            await _client.GetAsync($"/api/realtime/devices/search{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RealtimeDeviceSearchResponse>() ??
            throw new InvalidOperationException("Search returned no response body.");
    }

    private async Task<SensorResponse> RegisterAsync(
        string name,
        string identifier,
        string kind,
        string category,
        string dataType,
        string facility)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors",
            new RegisterSensorRequest(
                name,
                identifier,
                kind,
                new SensorDeploymentLocationRequest(facility, "Search Lab", "Node A"),
                category,
                dataType));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<SensorResponse>() ??
            throw new InvalidOperationException("Registration response missing.");
    }

    private async Task ChangeStatusAsync(Guid sensorId, string status)
    {
        HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{sensorId}/status",
            new ChangeSensorStatusRequest(status));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Uses the production DI graph with temporary, encrypted test storage.
    /// Dashboard reconciliation may access the attachment store; a test key
    /// prevents unrelated configuration failures from obscuring query tests.
    /// </summary>
    private sealed class DeviceSearchApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _storagePath;
        private readonly string _key;

        public DeviceSearchApiFactory(string storagePath, string key)
        {
            _storagePath = storagePath;
            _key = key;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Attachments:StoragePath", _storagePath);
            builder.UseSetting("Attachments:EncryptionKeyBase64", _key);
        }
    }
}