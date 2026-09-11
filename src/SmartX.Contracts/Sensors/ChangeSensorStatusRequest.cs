namespace SmartX.Contracts.Sensors;

/// <summary>
/// API-facing request used to simulate or report an operational state change
/// for a registered Smart-X sensor.
///
/// The transport layer intentionally accepts the status as text so
/// SmartX.Contracts remains independent from SmartX.Domain. The Application
/// layer performs controlled parsing into the SensorStatus enum.
/// </summary>
public sealed record ChangeSensorStatusRequest(
    string? Status);