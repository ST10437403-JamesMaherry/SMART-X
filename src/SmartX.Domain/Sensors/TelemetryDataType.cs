namespace SmartX.Domain.Sensors;

/// <summary>
/// Describes the expected telemetry value type produced by a sensor.
///
/// The actual telemetry payload will later be represented using the
/// generic TelemetryPacket<T> structure required by the PoE.
/// </summary>
public enum TelemetryDataType
{
    Float = 1,
    Integer = 2,
    Boolean = 3
}
