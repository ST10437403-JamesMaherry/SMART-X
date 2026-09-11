namespace SmartX.Contracts.Sensors;

/// <summary>
/// API representation of a validated Smart-X deployment location.
/// </summary>
public sealed record SensorDeploymentLocationResponse(
    string Facility,
    string Zone,
    string NodeId);