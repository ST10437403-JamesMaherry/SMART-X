namespace SmartX.Domain.Commands;

/// <summary>
/// Represents the commanded state of a simulated relay.
///
/// This is independent of sensor operational status and reported telemetry.
/// A device with no previous command has the explicit initial state Off.
/// </summary>
public enum RelayState
{
    Off = 0,
    On = 1
}