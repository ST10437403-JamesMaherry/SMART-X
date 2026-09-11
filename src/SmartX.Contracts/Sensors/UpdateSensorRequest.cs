namespace SmartX.Contracts.Sensors;

/// <summary>
/// API-facing request used to update the mutable details of a registered
/// Smart-X sensor.
///
/// Device identity is intentionally excluded because a physical sensor's
/// identifier is immutable after registration.
/// </summary>
public sealed record UpdateSensorRequest(
    string? Name,
    SensorDeploymentLocationRequest? DeploymentLocation,
    string? Category,
    string? TelemetryDataType);