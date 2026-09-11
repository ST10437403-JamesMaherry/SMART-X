namespace SmartX.Contracts.Telemetry;

/// <summary>
/// Summarises a successfully processed historical telemetry batch request.
/// </summary>
public sealed record TelemetryBatchIngestionResponse(
    Guid SensorProfileId,
    string TelemetryDataType,
    int BatchCount,
    int AcceptedPacketCount);