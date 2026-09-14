using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Health;

namespace SmartX.Api.IntegrationTests;

public sealed class HealthEndpointTests(
    WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AllowedClientOrigin = "http://localhost:5173";

    [Fact]
    public async Task GetHealth_WhenRequested_ReturnsHealthyContract()
    {
        // Arrange
        using var client = CreateHttpsClient();

        // Act
        using var response = await client.GetAsync("/api/health");
        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        Assert.NotNull(payload);
        Assert.Equal("Healthy", payload.Status);
        Assert.Equal("Smart-X API", payload.Service);

        Assert.InRange(
            payload.CheckedAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task GetHealth_FromConfiguredClient_ReturnsCorsHeader()
    {
        // Arrange
        using var client = CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", AllowedClientOrigin);

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            AllowedClientOrigin,
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task GetHealth_FromUnknownOrigin_DoesNotReturnCorsHeader()
    {
        // Arrange
        using var client = CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://untrusted.example");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private HttpClient CreateHttpsClient()
    {
        return factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
    }
}