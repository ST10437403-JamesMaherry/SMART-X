using SmartX.Contracts.Commands;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Contracts.Realtime;

/// <summary>
/// Immutable transport values for one registered Smart-X device.
///
/// Identity, metadata, and operational status come from the existing
/// authoritative sensor repository.
///
/// LatestProcessedTelemetry remains null until Part 2 telemetry
/// processing has actually completed.
///
/// CommandedActuator represents separate simulated relay state.
/// It is populated only for registered Actuator/Boolean devices.
/// An actuator's commanded state is not inferred from reported
/// telemetry or its current operational status.
/// </summary>
public sealed record RealtimeDeviceResponse(
    Guid Id,
    string Name,
    string DeviceIdentifier,
    string IdentifierKind,
    SensorDeploymentLocationResponse DeploymentLocation,
    string Category,
    string Status,
    string TelemetryDataType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    TelemetryReadingResponse? LatestProcessedTelemetry,
    DateTimeOffset? LastProcessedAtUtc,
    ActuatorCommandStateResponse? CommandedActuator = null);