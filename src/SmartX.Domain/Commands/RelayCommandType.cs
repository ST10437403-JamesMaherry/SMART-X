namespace SmartX.Domain.Commands;

/// <summary>
/// The deliberately small set of supported manual relay commands.
/// Additional device-specific aliases are unnecessary for Part 2.
/// </summary>
public enum RelayCommandType
{
    RelayOn = 1,
    RelayOff = 2
}