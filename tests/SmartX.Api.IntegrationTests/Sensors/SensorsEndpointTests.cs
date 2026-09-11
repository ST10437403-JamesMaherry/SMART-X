using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Sensors;

/// <summary>
/// Exercises the complete sensor HTTP pipeline using the real Application
/// services and Infrastructure repository.
///
/// Each xUnit test receives a fresh test-class instance and therefore a fresh
/// WebApplicationFactory, keeping sensor state isolated between tests.
/// </summary>
public sealed class SensorsEndpointTests
    : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SensorsEndpointTests()
    {
        _factory =
            new WebApplicationFactory<Program>();

        _client =
            _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress =
                        new Uri("https://localhost")
                });
    }

    [Fact]
    public async Task Register_WhenValid_ReturnsCreatedSensor()
    {
        var request =
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:20");

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        Assert.NotNull(
            response.Headers.Location);

        var sensor =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        Assert.NotNull(sensor);

        Assert.NotEqual(
            Guid.Empty,
            sensor.Id);

        Assert.Equal(
            "Greenhouse Sensor",
            sensor.Name);

        Assert.Equal(
            "AA:BB:CC:DD:EE:20",
            sensor.DeviceIdentifier);

        Assert.Equal(
            "Offline",
            sensor.Status);

        Assert.Equal(
            "Environmental",
            sensor.Category);

        Assert.Equal(
            "Float",
            sensor.TelemetryDataType);
    }

    [Fact]
    public async Task GetAll_WhenSensorsRegistered_ReturnsSensors()
    {
        await RegisterSensorAsync(
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:21"));

        await RegisterSensorAsync(
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:22")
            with
            {
                Name = "Power Sensor",
                Category = "PowerConsumption",
                TelemetryDataType = "Integer"
            });

        var response =
            await _client.GetAsync(
                "/api/sensors");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var sensors =
            await response.Content
                .ReadFromJsonAsync<SensorResponse[]>();

        Assert.NotNull(sensors);
        Assert.Equal(2, sensors.Length);
    }

    [Fact]
    public async Task GetById_WhenSensorExists_ReturnsSensor()
    {
        var registered =
            await RegisterSensorAsync(
                CreateRegistrationRequest(
                    "AA:BB:CC:DD:EE:23"));

        var response =
            await _client.GetAsync(
                $"/api/sensors/{registered.Id}");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var sensor =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        Assert.NotNull(sensor);

        Assert.Equal(
            registered.Id,
            sensor.Id);

        Assert.Equal(
            registered.DeviceIdentifier,
            sensor.DeviceIdentifier);
    }

    [Fact]
    public async Task GetById_WhenSensorMissing_ReturnsNotFoundProblemDetails()
    {
        var missingId =
            Guid.NewGuid();

        var response =
            await _client.GetAsync(
                $"/api/sensors/{missingId}");

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            problem.Status);

        Assert.Equal(
            "Sensor not found",
            problem.Title);

        Assert.Contains(
            missingId.ToString(),
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_WhenIdentifierDuplicate_ReturnsConflictProblemDetails()
    {
        var first =
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:24");

        await RegisterSensorAsync(first);

        // This is a different textual representation of the same physical
        // MAC address and must therefore be detected as a duplicate.
        var duplicate =
            CreateRegistrationRequest(
                "aa-bb-cc-dd-ee-24");

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                duplicate);

        Assert.Equal(
            HttpStatusCode.Conflict,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            StatusCodes.Status409Conflict,
            problem.Status);

        Assert.Equal(
            "Duplicate sensor identifier",
            problem.Title);
    }

    [Fact]
    public async Task Register_WhenMacAddressInvalid_ReturnsBadRequestProblemDetails()
    {
        var request =
            CreateRegistrationRequest(
                "not-a-valid-mac");

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            StatusCodes.Status400BadRequest,
            problem.Status);

        Assert.Contains(
            "MAC address",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_WhenDeploymentLocationInvalid_ReturnsBadRequest()
    {
        var request =
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:25")
            with
            {
                DeploymentLocation =
                    new SensorDeploymentLocationRequest(
                        "",
                        "Zone 1",
                        "Node 1")
            };

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Contains(
            "Facility",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_WhenCategoryInvalid_ReturnsBadRequest()
    {
        var request =
            CreateRegistrationRequest(
                "AA:BB:CC:DD:EE:26")
            with
            {
                Category = "NotARealCategory"
            };

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Contains(
            "sensor category",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_WhenValid_ReturnsUpdatedSensorAndPreservesIdentifier()
    {
        var registered =
            await RegisterSensorAsync(
                CreateRegistrationRequest(
                    "AA:BB:CC:DD:EE:27"));

        var request =
            new UpdateSensorRequest(
                "Updated Power Sensor",
                new SensorDeploymentLocationRequest(
                    "Facility B",
                    "Zone 4",
                    "Node 9"),
                "PowerConsumption",
                "Integer");

        var response =
            await _client.PutAsJsonAsync(
                $"/api/sensors/{registered.Id}",
                request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var updated =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        Assert.NotNull(updated);

        Assert.Equal(
            registered.Id,
            updated.Id);

        Assert.Equal(
            registered.DeviceIdentifier,
            updated.DeviceIdentifier);

        Assert.Equal(
            "Updated Power Sensor",
            updated.Name);

        Assert.Equal(
            "Facility B",
            updated.DeploymentLocation.Facility);

        Assert.Equal(
            "PowerConsumption",
            updated.Category);

        Assert.Equal(
            "Integer",
            updated.TelemetryDataType);

        Assert.True(
            updated.UpdatedAtUtc >=
            updated.CreatedAtUtc);
    }

    [Fact]
    public async Task Update_WhenSensorMissing_ReturnsNotFound()
    {
        var request =
            new UpdateSensorRequest(
                "Updated Sensor",
                new SensorDeploymentLocationRequest(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                "Environmental",
                "Float");

        var response =
            await _client.PutAsJsonAsync(
                $"/api/sensors/{Guid.NewGuid()}",
                request);

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);
    }

    [Fact]
    public async Task Update_WhenCategoryInvalid_ReturnsBadRequestProblemDetails()
    {
        var registered =
            await RegisterSensorAsync(
                CreateRegistrationRequest(
                    "AA:BB:CC:DD:EE:28"));

        var request =
            new UpdateSensorRequest(
                "Invalid Update",
                new SensorDeploymentLocationRequest(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                "UnknownCategory",
                "Float");

        var response =
            await _client.PutAsJsonAsync(
                $"/api/sensors/{registered.Id}",
                request);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);

        Assert.Equal(
            StatusCodes.Status400BadRequest,
            problem.Status);

        Assert.Contains(
            "sensor category",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_WhenJsonMalformed_ReturnsBadRequest()
    {
        using var content =
            new StringContent(
                """
                {
                  "name": "Broken JSON",
                  "deviceIdentifier":
                }
                """,
                System.Text.Encoding.UTF8,
                "application/json");

        var response =
            await _client.PostAsync(
                "/api/sensors",
                content);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<SensorResponse> RegisterSensorAsync(
        RegisterSensorRequest request)
    {
        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        response.EnsureSuccessStatusCode();

        var sensor =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "The sensor registration response was empty.");
    }

    private static RegisterSensorRequest CreateRegistrationRequest(
        string macAddress)
    {
        return new RegisterSensorRequest(
            "Greenhouse Sensor",
            macAddress,
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Facility A",
                "Zone 1",
                "Node 1"),
            "Environmental",
            "Float");
    }
}