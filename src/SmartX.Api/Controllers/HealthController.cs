using Microsoft.AspNetCore.Mvc;

using SmartX.Contracts.Health;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes the current availability of the Smart-X API.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class HealthController(TimeProvider timeProvider) : ControllerBase
{
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Returns a lightweight API health response.
    /// </summary>
    /// <returns>The current API status and check time.</returns>
    [HttpGet(Name = "GetApiHealth")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get()
    {
        var response = new HealthResponse(
            Status: "Healthy",
            Service: "Smart-X API",
            CheckedAtUtc: _timeProvider.GetUtcNow());

        return Ok(response);
    }
}