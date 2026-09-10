namespace SmartX.Domain.Sensors;

/// <summary>
/// High-level categories supported by the Smart-X ingestion gateway.
///
/// These categories align with the Part 1 Smart-X sensor registration
/// requirements and can later be used for filtering and dashboard grouping.
/// </summary>
public enum SensorCategory
{
    Environmental = 1,
    PowerConsumption = 2,
    Actuator = 3
}