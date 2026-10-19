using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Distinguishes a valid physical identifier with no registered device from
/// malformed lookup input. The API translates this expected failure to 404.
/// </summary>
public sealed class RealtimeDeviceNotFoundException : Exception
{
    public RealtimeDeviceNotFoundException(DeviceIdentifier deviceIdentifier)
        : base(CreateMessage(deviceIdentifier))
    {
    }

    private static string CreateMessage(DeviceIdentifier deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentifier);

        return $"No registered device matches " +
            $"'{deviceIdentifier.Kind}:{deviceIdentifier.Value}'.";
    }
}