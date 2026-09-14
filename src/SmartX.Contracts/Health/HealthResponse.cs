namespace SmartX.Contracts.Health;

/// <summary>
/// Represents the current availability of the Smart-X API.
/// </summary>
/// <param name="Status">The current health status.</param>
/// <param name="Service">The name of the responding service.</param>
/// <param name="CheckedAtUtc">The UTC time at which the health check was created.</param>
public sealed record HealthResponse(
    string Status,
    string Service,
    DateTimeOffset CheckedAtUtc);