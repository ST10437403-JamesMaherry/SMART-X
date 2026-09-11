using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Raised when a registration attempts to persist a physical device
/// identifier that is already registered.
///
/// Device identifiers are checked after domain normalisation, ensuring
/// equivalent MAC address representations cannot be registered separately.
/// </summary>
public sealed class DuplicateSensorIdentifierException : Exception
{
    public DuplicateSensorIdentifierException(
        DeviceIdentifier deviceIdentifier)
        : this(GetIdentifierValue(deviceIdentifier))
    {
    }

    private DuplicateSensorIdentifierException(
        string deviceIdentifier)
        : base(
            $"A sensor with device identifier " +
            $"'{deviceIdentifier}' is already registered.")
    {
        DeviceIdentifier = deviceIdentifier;
    }

    /// <summary>
    /// Normalised device identifier involved in the duplicate registration.
    /// </summary>
    public string DeviceIdentifier { get; }

    private static string GetIdentifierValue(
        DeviceIdentifier deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentifier);

        return deviceIdentifier.Value;
    }
}