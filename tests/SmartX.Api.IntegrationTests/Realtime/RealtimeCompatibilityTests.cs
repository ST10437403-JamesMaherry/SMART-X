using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Completes Part 2 identifier and snapshot compatibility coverage through
/// the actual HTTP pipeline. Each test has its own registered-sensor state.
/// </summary>
public sealed class RealtimeCompatibilityTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RealtimeCompatibilityTests()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:81")]
    [InlineData("aa-bb-cc-dd-ee-81")]
    [InlineData("aabbccddee81")]
    [InlineData("  aa:bb:cc:dd:ee:81  ")]
    public async Task GetDevices_WhenMacFormatsEquivalent_ReturnsTheSameRegisteredGuid(
        string lookupValue)
    {
        var registered = await RegisterAsync("MacAddress", "AA:BB:CC:DD:EE:81");

        // Kind names follow the existing case-insensitive transport parser.
        // The physical value itself follows the Domain MAC canonicalisation.
        var device = Assert.Single(await LookupAsync(" macaddress ", lookupValue));
        var legacy = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{registered.Id}");

        Assert.NotNull(legacy);
        Assert.Equal(registered.Id, device.Id);
        Assert.Equal(legacy.Id, device.Id);
        Assert.Equal("MacAddress", device.IdentifierKind);
        Assert.Equal("AA:BB:CC:DD:EE:81", device.DeviceIdentifier);
        Assert.Equal(registered.DeploymentLocation, device.DeploymentLocation);
    }

    [Fact]
    public async Task GetDevices_WhenCustomIdsDifferOnlyByCase_PreservesBothIdentities()
    {
        var upper = await RegisterAsync("UniqueIdentifier", "Node-Case");
        var lower = await RegisterAsync("UniqueIdentifier", "node-case");

        var upperMatch = Assert.Single(await LookupAsync("uniqueidentifier", "  Node-Case  "));
        var lowerMatch = Assert.Single(await LookupAsync("UniqueIdentifier", "node-case"));

        Assert.NotEqual(upper.Id, lower.Id);
        Assert.Equal(upper.Id, upperMatch.Id);
        Assert.Equal(lower.Id, lowerMatch.Id);
        Assert.Equal("Node-Case", upperMatch.DeviceIdentifier);
        Assert.Equal("node-case", lowerMatch.DeviceIdentifier);

        // Case folding would incorrectly turn this missing identifier into
        // one of the two registered devices.
        var missing = await _client.GetAsync(LookupPath("UniqueIdentifier", "NODE-CASE"));
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "Device not found");

        Assert.Equal(2, (await GetSnapshotAsync()).DeviceCount);
    }

    [Fact]
    public async Task GetDevices_WhenRawIdentifierSharedAcrossKinds_ResolvesSeparateDevices()
    {
        const string sharedValue = "AABBCCDDEE82";

        var mac = await RegisterAsync("MacAddress", sharedValue);
        var custom = await RegisterAsync("UniqueIdentifier", sharedValue);

        var macMatch = Assert.Single(await LookupAsync("MacAddress", sharedValue));
        var customMatch = Assert.Single(await LookupAsync("UniqueIdentifier", sharedValue));

        Assert.Equal(mac.Id, macMatch.Id);
        Assert.Equal(custom.Id, customMatch.Id);
        Assert.NotEqual(macMatch.Id, customMatch.Id);
        Assert.Equal("AA:BB:CC:DD:EE:82", macMatch.DeviceIdentifier);
        Assert.Equal(sharedValue, customMatch.DeviceIdentifier);
        Assert.Equal("MacAddress", macMatch.IdentifierKind);
        Assert.Equal("UniqueIdentifier", customMatch.IdentifierKind);
        Assert.Equal(2, (await GetSnapshotAsync()).DeviceCount);
    }

    [Theory]
    [InlineData("?deviceIdentifier=AABBCCDDEE83")]
    [InlineData("?identifierKind=MacAddress")]
    [InlineData("?identifierKind=&deviceIdentifier=")]
    [InlineData("?identifierKind=MacAddress&deviceIdentifier=")]
    [InlineData("?identifierKind=&deviceIdentifier=AABBCCDDEE83")]
    [InlineData("?identifierKind=Unknown&deviceIdentifier=valid-device")]
    [InlineData("?identifierKind=1&deviceIdentifier=AABBCCDDEE83")]
    [InlineData("?identifierKind=MacAddress&deviceIdentifier=AA%3ABB%3ACC%3ADD%3AEE%3AGG")]
    [InlineData("?identifierKind=UniqueIdentifier&deviceIdentifier=Node%2Fbad")]
    [InlineData("?identifierKind=UniqueIdentifier&deviceIdentifier=%20%20")]
    public async Task GetDevices_WhenLookupQueryInvalid_ReturnsBadRequestWithoutChangingRegistry(
        string query)
    {
        var registered = await RegisterAsync("MacAddress", "AA:BB:CC:DD:EE:83");

        var response = await _client.GetAsync($"/api/realtime/devices{query}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        // A partial or empty query must fail rather than silently returning
        // the unfiltered registered-device collection.
        var snapshot = await GetSnapshotAsync();

        Assert.Equal(1, snapshot.DeviceCount);
        Assert.Equal(registered.Id, Assert.Single(snapshot.Devices).Id);
    }

    [Fact]
    public async Task GetDevices_WhenCustomIdentifierTooLong_ReturnsBadRequest()
    {
        var tooLong = new string('n', 65);

        var response = await _client.GetAsync(LookupPath("UniqueIdentifier", tooLong));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Domain validation failed");

        Assert.Empty((await GetSnapshotAsync()).Devices);
    }

    [Fact]
    public async Task GetDevices_WhenCanonicalMacUnregistered_ReturnsNotFound()
    {
        await RegisterAsync("MacAddress", "AA:BB:CC:DD:EE:84");

        var response = await _client.GetAsync(LookupPath("MacAddress", "aabbccddee85"));
        var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, "Device not found");

        Assert.Contains("AA:BB:CC:DD:EE:85", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(1, (await GetSnapshotAsync()).DeviceCount);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:86")]
    [InlineData("aa-bb-cc-dd-ee-86")]
    [InlineData("AABBCCDDEE86")]
    public async Task Register_WhenMacRepresentationDuplicatesExistingDevice_PreservesConflictContract(
        string duplicateValue)
    {
        var registered = await RegisterAsync("MacAddress", "AA:BB:CC:DD:EE:86");

        var response = await _client.PostAsJsonAsync(
            "/api/sensors",
            CreateRequest("MacAddress", duplicateValue) with { Name = "Rejected duplicate" });

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "Duplicate sensor identifier",
            "/api/sensors");

        var device = Assert.Single(await LookupAsync("MacAddress", duplicateValue));
        var snapshot = await GetSnapshotAsync();
        var legacy = await _client.GetFromJsonAsync<SensorResponse[]>("/api/sensors");

        Assert.NotNull(legacy);
        Assert.Equal(registered.Id, Assert.Single(legacy).Id);
        Assert.Equal(registered.Id, device.Id);
        Assert.Equal(registered.Name, device.Name);
        Assert.Equal(1, snapshot.DeviceCount);
        Assert.Equal(device, Assert.Single(snapshot.Devices));
    }

    [Fact]
    public async Task Register_WhenCustomIdentifierExactlyDuplicatesExistingDevice_ReturnsConflict()
    {
        var registered = await RegisterAsync("UniqueIdentifier", "custom-device");

        var response = await _client.PostAsJsonAsync(
            "/api/sensors",
            CreateRequest("UniqueIdentifier", "custom-device"));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "Duplicate sensor identifier",
            "/api/sensors");

        Assert.Equal(
            registered.Id,
            Assert.Single(await LookupAsync("UniqueIdentifier", "custom-device")).Id);

        Assert.Equal(1, (await GetSnapshotAsync()).DeviceCount);
    }

    [Fact]
    public async Task Register_WhenEquivalentMacRequestsConcurrent_PublishesOnlyOneDevice()
    {
        string[] formats = ["AA:BB:CC:DD:EE:87", "aa-bb-cc-dd-ee-87", "aabbccddee87"];

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Real simultaneous HTTP requests exercise the scoped registration
        // services against the shared synchronized repository.
        var attempts = Enumerable.Range(0, 8)
            .Select(async index =>
            {
                await start.Task;

                return await _client.PostAsJsonAsync(
                    "/api/sensors",
                    CreateRequest("MacAddress", formats[index % formats.Length]) with
                    {
                        Name = $"Concurrent device {index}"
                    });
            })
            .ToArray();

        start.SetResult(true);

        var responses = await Task.WhenAll(attempts);
        var created = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);

        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var winner = await created.Content.ReadFromJsonAsync<SensorResponse>();
        var snapshot = await GetSnapshotAsync();
        var matched = Assert.Single(await LookupAsync("MacAddress", "AABBCCDDEE87"));

        Assert.NotNull(winner);
        Assert.Equal(1, snapshot.DeviceCount);
        Assert.Equal(winner.Id, Assert.Single(snapshot.Devices).Id);
        Assert.Equal(winner.Id, matched.Id);
        Assert.Equal(winner.Name, matched.Name);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task GetSnapshot_WhenReadRepeatedly_PreservesDeviceValuesAndLegacyOrdering()
    {
        await RegisterAsync("UniqueIdentifier", "first-device");
        await RegisterAsync("MacAddress", "AA:BB:CC:DD:EE:88");

        var first = await GetSnapshotAsync();
        var second = await GetSnapshotAsync();
        var legacy = await _client.GetFromJsonAsync<SensorResponse[]>("/api/sensors");

        Assert.NotNull(legacy);
        Assert.Equal(2, first.DeviceCount);
        Assert.Equal(first.DeviceCount, second.DeviceCount);
        Assert.Equal(first.Devices.ToArray(), second.Devices.ToArray());
        Assert.Equal(legacy.Select(sensor => sensor.Id), second.Devices.Select(device => device.Id));
        Assert.Equal(TimeSpan.Zero, first.GeneratedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, second.GeneratedAtUtc.Offset);
    }

    [Fact]
    public async Task GetSnapshot_WhenDeviceRegisteredAfterFirstRead_ExposesNewMembership()
    {
        var empty = await GetSnapshotAsync();
        var first = await RegisterAsync("UniqueIdentifier", "snapshot-first");
        var single = await GetSnapshotAsync();
        var second = await RegisterAsync("UniqueIdentifier", "snapshot-second");
        var current = await GetSnapshotAsync();

        Assert.Equal(0, empty.DeviceCount);
        Assert.Empty(empty.Devices);
        Assert.Equal(1, single.DeviceCount);
        Assert.Equal(first.Id, Assert.Single(single.Devices).Id);
        Assert.Equal(2, current.DeviceCount);
        Assert.Contains(current.Devices, device => device.Id == first.Id);
        Assert.Contains(current.Devices, device => device.Id == second.Id);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static RegisterSensorRequest CreateRequest(string identifierKind, string identifier)
    {
        return new RegisterSensorRequest(
            "Compatibility Device",
            identifier,
            identifierKind,
            new SensorDeploymentLocationRequest("Facility A", "Zone 1", "Node 1"),
            "Environmental",
            "Float");
    }

    private async Task<SensorResponse> RegisterAsync(string identifierKind, string identifier)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/sensors",
            CreateRequest(identifierKind, identifier));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        return await response.Content.ReadFromJsonAsync<SensorResponse>() ??
            throw new InvalidOperationException("Sensor registration returned no response.");
    }

    private static string LookupPath(string identifierKind, string identifier)
    {
        return $"/api/realtime/devices?identifierKind={Uri.EscapeDataString(identifierKind)}" +
            $"&deviceIdentifier={Uri.EscapeDataString(identifier)}";
    }

    private async Task<RealtimeDeviceResponse[]> LookupAsync(string identifierKind, string identifier)
    {
        var response = await _client.GetAsync(LookupPath(identifierKind, identifier));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RealtimeDeviceResponse[]>() ??
            throw new InvalidOperationException("Device lookup returned no response.");
    }

    private async Task<RealtimeSnapshotResponse> GetSnapshotAsync()
    {
        var response = await _client.GetAsync("/api/realtime/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RealtimeSnapshotResponse>() ??
            throw new InvalidOperationException("Snapshot endpoint returned no response.");
    }

    private static async Task<ProblemDetails> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string? expectedTitle = null,
        string expectedPath = "/api/realtime/devices")
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.NotNull(problem.Detail);
        Assert.Equal(expectedPath, problem.Instance);
        Assert.True(problem.Extensions.ContainsKey("traceId"));

        if (expectedTitle is not null)
        {
            Assert.Equal(expectedTitle, problem.Title);
        }

        return problem;
    }
}