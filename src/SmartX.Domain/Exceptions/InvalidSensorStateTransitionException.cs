using SmartX.Domain.Sensors;

namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when an invalid sensor status transition is attempted.
/// </summary>
public sealed class InvalidSensorStateTransitionException : DomainValidationException
{
    public InvalidSensorStateTransitionException(
        SensorStatus currentStatus,
        SensorStatus requestedStatus)
        : base(
            $"Sensor status cannot change from '{currentStatus}' " +
            $"to '{requestedStatus}'.")
    {
        CurrentStatus = currentStatus;
        RequestedStatus = requestedStatus;
    }

    /// <summary>
    /// Status of the sensor before the rejected transition.
    /// </summary>
    public SensorStatus CurrentStatus { get; }

    /// <summary>
    /// Status that the caller attempted to assign.
    /// </summary>
    public SensorStatus RequestedStatus { get; }
}
