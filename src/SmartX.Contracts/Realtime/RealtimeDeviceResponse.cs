using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Contracts.Realtime;

/// <summary>
/// Immutable transport values for one registered device. Metadata and status
/// come from the existing sensor authority, not a second device registry.
/// Processing fields remain null until the Part 2 worker processes telemetry;
/// accepted Part 1 telemetry is not misrepresented as processed work.
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
    DateTimeOffset? LastProcessedAtUtc);