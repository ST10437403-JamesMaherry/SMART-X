namespace SmartX.Contracts.Sensors;

/// <summary>
/// API-facing request used to register a Smart-X sensor.
///
/// String values are intentionally used for transport-facing enum fields.
/// This keeps SmartX.Contracts independent from SmartX.Domain while the
/// application mapper performs controlled conversion into domain types.
/// </summary>
public sealed record RegisterSensorRequest(
    string? Name,
    string? DeviceIdentifier,
    string? IdentifierKind,
    SensorDeploymentLocationRequest? DeploymentLocation,
    string? Category,
    string? TelemetryDataType);