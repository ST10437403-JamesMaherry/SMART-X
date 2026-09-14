using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartX.Api.IntegrationTests.Security;

/// <summary>
/// Verifies the browser-facing Smart-X CORS policy through the real ASP.NET
/// Core middleware pipeline.
///
/// Development permits the local Vite client while unrelated origins must not
/// receive Access-Control-Allow-Origin permission.
///
/// CORS is a browser security boundary rather than an authentication system:
/// the server may still return a normal HTTP response to a disallowed origin,
/// but the absence of the CORS response header prevents browser JavaScript
/// from reading that response cross-origin.
/// </summary>
public sealed class CorsPolicyTests
    : IDisposable
{
    private const string AllowedClientOrigin =
        "http://localhost:5173";

    private const string DisallowedOrigin =
        "https://untrusted.example";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CorsPolicyTests()
    {
        /*
         * appsettings.Development.json contains the local React/Vite origin,
         * so explicitly host the integration application in Development when
         * verifying that configuration.
         */
        _factory =
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(
                    builder =>
                    {
                        builder.UseEnvironment(
                            "Development");
                    });

        /*
         * Use an HTTPS base address because the production middleware pipeline
         * includes HTTPS redirection. This keeps the test focused exclusively
         * on CORS rather than following an unrelated redirect.
         */
        _client =
            _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress =
                        new Uri(
                            "https://localhost"),

                    AllowAutoRedirect =
                        false
                });
    }

    [Fact]
    public async Task Preflight_WhenOriginConfigured_AllowsReactClient()
    {
        using var request =
            CreatePreflightRequest(
                AllowedClientOrigin);

        using HttpResponseMessage response =
            await _client.SendAsync(
                request);

        Assert.Equal(
            HttpStatusCode.NoContent,
            response.StatusCode);

        Assert.True(
            response.Headers.TryGetValues(
                "Access-Control-Allow-Origin",
                out IEnumerable<string>? origins));

        Assert.Equal(
            AllowedClientOrigin,
            Assert.Single(origins));

        Assert.True(
            response.Headers.TryGetValues(
                "Access-Control-Allow-Methods",
                out IEnumerable<string>? methods));

        Assert.Contains(
            methods,
            value =>
                value.Contains(
                    "GET",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preflight_WhenOriginNotConfigured_DoesNotGrantCorsAccess()
    {
        using var request =
            CreatePreflightRequest(
                DisallowedOrigin);

        using HttpResponseMessage response =
            await _client.SendAsync(
                request);

        /*
         * ASP.NET Core does not need to return an error status for a rejected
         * CORS origin. Browser access is denied by omitting the allow-origin
         * response header.
         */
        Assert.False(
            response.Headers.Contains(
                "Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// Builds the same OPTIONS request a browser sends before a cross-origin
    /// API operation.
    /// </summary>
    private static HttpRequestMessage CreatePreflightRequest(
        string origin)
    {
        var request =
            new HttpRequestMessage(
                HttpMethod.Options,
                "/api/health");

        request.Headers.Add(
            "Origin",
            origin);

        request.Headers.Add(
            "Access-Control-Request-Method",
            "GET");

        return request;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}