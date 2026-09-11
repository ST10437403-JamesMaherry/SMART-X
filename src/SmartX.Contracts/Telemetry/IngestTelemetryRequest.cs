namespace SmartX.Contracts.Telemetry;

/// <summary>
/// Represents one incoming telemetry reading.
///
/// Smart-X supports three strongly typed payload categories during Part 1.
/// Exactly one payload property must contain a value and it must correspond
/// to the TelemetryDataType configured for the registered sensor.
///
/// Nullable properties are used at the HTTP boundary so that legitimate
/// values such as 0 and false remain distinguishable from missing values.
/// </summary>
public sealed record IngestTelemetryRequest(
    DateTimeOffset? CapturedAtUtc,
    double? FloatValue,
    int? IntegerValue,
    bool? BooleanValue);