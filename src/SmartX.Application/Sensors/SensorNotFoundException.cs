namespace SmartX.Application.Sensors;

/// <summary>
/// Raised when an application operation targets a sensor profile that does
/// not exist.
///
/// Keeping this distinction at the Application boundary allows the API to
/// translate the condition consistently into HTTP 404 ProblemDetails.
/// </summary>
public sealed class SensorNotFoundException : Exception
{
    public SensorNotFoundException(Guid sensorProfileId)
        : base(
            $"Sensor profile '{sensorProfileId}' was not found.")
    {
        SensorProfileId = sensorProfileId;
    }

    /// <summary>
    /// Sensor profile identifier that could not be resolved.
    /// </summary>
    public Guid SensorProfileId { get; }
}