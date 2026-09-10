namespace SmartX.Domain.Sensors;

/// <summary>
/// Represents the current operational state of a registered Smart-X sensor.
/// </summary>
public enum SensorStatus
{
    Offline = 1,
    Online = 2,
    Warning = 3,
    Fault = 4,
    Maintenance = 5,
    Decommissioned = 6
}