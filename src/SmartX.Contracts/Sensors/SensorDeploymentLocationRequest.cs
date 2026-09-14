namespace SmartX.Contracts.Sensors;

/// <summary>
/// Transport contract describing where a sensor is physically deployed.
///
/// Validation of these values remains the responsibility of the Smart-X
/// domain model after the API contract enters the application layer.
/// </summary>
public sealed record SensorDeploymentLocationRequest(
    string? Facility,
    string? Zone,
    string? NodeId);