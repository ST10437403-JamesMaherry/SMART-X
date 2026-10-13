using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Runs the new reads through the real API, Application service and canonical
/// Dictionary repository. Every test has an isolated in-memory API host.
/// </summary>
public sealed class RealtimeEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RealtimeEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task GetSnapshot_WhenRegistryEmpty_ReturnsEmptyDeviceCollection()
    {
        var snapshot = await GetSnapshotAsync();
        var devices = await GetDevicesAsync();

        Assert.Equal(0, snapshot.DeviceCount);
        Assert.Empty(snapshot.Devices);
        Assert.Empty(devices);
        Assert.NotEqual(default, snapshot.GeneratedAtUtc);
        Assert.Equal(TimeSpan.Zero, snapshot.GeneratedAtUtc.Offset);
    }

    [Fact]
    public async Task GetDevices_WhenSensorsRegistered_ListsThemAndFindsCanonicalMac()
    {
        var first = await RegisterAsync("AA:BB:CC:DD:EE:71");
        var second = await RegisterAsync("AA:BB:CC:DD:EE:72");

        var legacy = await _client.GetFromJsonAsync<SensorResponse[]>("/api/sensors");
        var devices = await GetDevicesAsync();
        var snapshot = await GetSnapshotAsync();

        Assert.NotNull(legacy);
        Assert.Equal(2, snapshot.DeviceCount);
        Assert.Equal(legacy.Select(sensor => sensor.Id), devices.Select(device => device.Id));
        Assert.Equal(devices.Select(device => device.Id), snapshot.Devices.Select(device => device.Id));

        var matches = await GetDevicesAsync(
            "?identifierKind=MacAddress&deviceIdentifier=aabbccddee71");

        var matched = Assert.Single(matches);

        Assert.Equal(first.Id, matched.Id);
        Assert.Equal("AA:BB:CC:DD:EE:71", matched.DeviceIdentifier);
        Assert.Equal(first.IdentifierKind, matched.IdentifierKind);
        Assert.Equal(first.Name, matched.Name);
        Assert.Equal(first.DeploymentLocation, matched.DeploymentLocation);
        Assert.Equal(first.Category, matched.Category);
        Assert.Equal(first.TelemetryDataType, matched.TelemetryDataType);
        Assert.Equal(first.CreatedAtUtc, matched.CreatedAtUtc);
        Assert.Equal(first.UpdatedAtUtc, matched.UpdatedAtUtc);
        Assert.Equal("Offline", matched.Status);
        Assert.Null(matched.LatestProcessedTelemetry);
        Assert.Null(matched.LastProcessedAtUtc);
        Assert.Contains(devices, device => device.Id == second.Id);
    }

    [Fact]
    public async Task GetSnapshot_WhenLegacyMetadataAndStatusChange_ReturnsCurrentProfile()
    {
        var sensor = await RegisterAsync("AA:BB:CC:DD:EE:73");
        var before = await GetSnapshotAsync();

        var update = new UpdateSensorRequest(
            "Updated Power Device",
            new SensorDeploymentLocationRequest("Facility B", "Zone 4", "Node 9"),
            "PowerConsumption",
            "Integer");

        var updateResponse = await _client.PutAsJsonAsync($"/api/sensors/{sensor.Id}", update);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var statusResponse = await _client.PatchAsJsonAsync(
            $"/api/sensors/{sensor.Id}/status",
            new ChangeSensorStatusRequest("Warning"));

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var legacy = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensor.Id}");
        var current = Assert.Single((await GetSnapshotAsync()).Devices);
        var exact = Assert.Single(await GetDevicesAsync(
            "?identifierKind=MacAddress&deviceIdentifier=aa-bb-cc-dd-ee-73"));

        Assert.NotNull(legacy);
        Assert.Equal(sensor.Id, current.Id);
        Assert.Equal(sensor.DeviceIdentifier, current.DeviceIdentifier);
        Assert.Equal("Updated Power Device", current.Name);
        Assert.Equal("Facility B", current.DeploymentLocation.Facility);
        Assert.Equal("Zone 4", current.DeploymentLocation.Zone);
        Assert.Equal("Node 9", current.DeploymentLocation.NodeId);
        Assert.Equal("PowerConsumption", current.Category);
        Assert.Equal("Integer", current.TelemetryDataType);
        Assert.Equal("Warning", current.Status);
        Assert.Equal(legacy.UpdatedAtUtc, current.UpdatedAtUtc);
        Assert.Equal(current, exact);

        // The earlier HTTP snapshot contains values, not a live entity view.
        Assert.Equal(sensor.Name, Assert.Single(before.Devices).Name);
        Assert.Equal("Offline", Assert.Single(before.Devices).Status);
    }

    [Fact]
    public async Task GetSnapshot_WhenPartOneTelemetryAccepted_ReflectsOnlineTransition()
    {
        var sensor = await RegisterAsync("AA:BB:CC:DD:EE:74");

        var response = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensor.Id}/telemetry",
            new IngestTelemetryRequest(DateTimeOffset.UtcNow.AddSeconds(-5), 21.5, null, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var accepted = await response.Content.ReadFromJsonAsync<TelemetryReadingResponse>();
        var device = Assert.Single((await GetSnapshotAsync()).Devices);

        Assert.NotNull(accepted);
        Assert.Equal(sensor.Id, accepted.SensorProfileId);
        Assert.Equal("Online", device.Status);

        // Acceptance and processing are deliberately separate. Wait for a
        // completion condition instead of assuming the worker beats HTTP.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (device.LatestProcessedTelemetry is null)
        {
            await Task.Delay(10, timeout.Token);
            device = Assert.Single((await GetSnapshotAsync()).Devices);
        }
        Assert.Equal(accepted, device.LatestProcessedTelemetry);
        Assert.NotNull(device.LastProcessedAtUtc);
    }

    [Fact]
    public async Task GetDevices_WhenIdentifierValidButMissing_ReturnsNotFoundProblemDetails()
    {
        var response = await _client.GetAsync(
            "/api/realtime/devices?identifierKind=UniqueIdentifier&deviceIdentifier=missing-device");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("Device not found", problem.Title);
        Assert.Contains("missing-device", problem.Detail, StringComparison.Ordinal);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task GetDevices_WhenMacInvalid_ReturnsBadRequestProblemDetails()
    {
        var response = await _client.GetAsync(
            "/api/realtime/devices?identifierKind=MacAddress&deviceIdentifier=not-a-mac");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Domain validation failed", problem.Title);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<SensorResponse> RegisterAsync(string macAddress)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/sensors",
            new RegisterSensorRequest(
                "Registry Device",
                macAddress,
                "MacAddress",
                new SensorDeploymentLocationRequest("Facility A", "Zone 1", "Node 1"),
                "Environmental",
                "Float"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<SensorResponse>() ??
            throw new InvalidOperationException("Sensor registration returned no response.");
    }

    private async Task<RealtimeDeviceResponse[]> GetDevicesAsync(string query = "")
    {
        var response = await _client.GetAsync($"/api/realtime/devices{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RealtimeDeviceResponse[]>() ??
            throw new InvalidOperationException("Device listing returned no response.");
    }

    private async Task<RealtimeSnapshotResponse> GetSnapshotAsync()
    {
        var response = await _client.GetAsync("/api/realtime/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RealtimeSnapshotResponse>() ??
            throw new InvalidOperationException("Snapshot endpoint returned no response.");
    }
}