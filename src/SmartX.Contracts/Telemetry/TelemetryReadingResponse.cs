namespace SmartX.Contracts.Telemetry;

/// <summary>
/// API-safe representation of one stored telemetry reading.
///
/// Only the strongly typed value corresponding to TelemetryDataType is
/// populated. The remaining payload properties are null.
/// </summary>
public sealed record TelemetryReadingResponse(
    Guid SensorProfileId,
    string DeviceIdentifier,
    string TelemetryDataType,
    double? FloatValue,
    int? IntegerValue,
    bool? BooleanValue,
    DateTimeOffset CapturedAtUtc);