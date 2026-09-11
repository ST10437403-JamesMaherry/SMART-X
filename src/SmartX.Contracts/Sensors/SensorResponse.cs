namespace SmartX.Contracts.Sensors;

/// <summary>
/// API representation of a registered Smart-X sensor profile.
///
/// Domain objects are deliberately not exposed directly through the API.
/// This protects the domain model from transport concerns and allows API
/// contracts to evolve independently.
/// </summary>
public sealed record SensorResponse(
    Guid Id,
    string Name,
    string DeviceIdentifier,
    string IdentifierKind,
    SensorDeploymentLocationResponse DeploymentLocation,
    string Category,
    string Status,
    string TelemetryDataType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);