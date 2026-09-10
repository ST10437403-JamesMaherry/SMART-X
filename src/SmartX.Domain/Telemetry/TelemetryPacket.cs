using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

namespace SmartX.Domain.Telemetry;

/// <summary>
/// Represents a single strongly typed telemetry reading received from
/// a physical Smart-X device.
///
/// The generic payload keeps values in their native value type. For example,
/// a temperature reading can remain a double, a power reading can remain an
/// int, and a switch state can remain a bool without converting the payload
/// to object or using boxing/unboxing.
///
/// T is restricted to value types because Smart-X telemetry in the current
/// domain consists of compact sensor values suitable for high-throughput
/// gateway processing.
/// </summary>
/// <typeparam name="T">
/// The strongly typed telemetry payload, such as double, int or bool.
/// </typeparam>
public class TelemetryPacket<T>
    where T : struct
{
    /// <summary>
    /// Creates a new immutable telemetry packet.
    /// </summary>
    /// <param name="sourceDeviceIdentifier">
    /// MAC address or custom unique identifier of the physical device that
    /// produced the telemetry.
    /// </param>
    /// <param name="payload">
    /// Strongly typed telemetry value produced by the device.
    /// </param>
    /// <param name="capturedAtUtc">
    /// Time at which the reading was captured by the device.
    /// The value is normalised to UTC.
    /// </param>
    /// <exception cref="InvalidTelemetryPacketException">
    /// Thrown when required packet metadata is invalid.
    /// </exception>
    public TelemetryPacket(
        DeviceIdentifier? sourceDeviceIdentifier,
        T payload,
        DateTimeOffset capturedAtUtc)
    {
        if (sourceDeviceIdentifier is null)
        {
            throw new InvalidTelemetryPacketException(
                "A telemetry packet must identify its source device.");
        }

        if (capturedAtUtc == default)
        {
            throw new InvalidTelemetryPacketException(
                "A telemetry packet must contain a valid capture timestamp.");
        }

        SourceDeviceIdentifier = sourceDeviceIdentifier;
        Payload = payload;

        // Normalising at the domain boundary gives later queues, histories
        // and timeline queries one consistent time basis.
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
    }

    /// <summary>
    /// Identifies the sensor or IoT node that produced this packet.
    ///
    /// This deliberately uses the existing DeviceIdentifier value object,
    /// meaning incoming telemetry uses the same canonical device identity
    /// as registered sensor profiles.
    /// </summary>
    public DeviceIdentifier SourceDeviceIdentifier { get; }

    /// <summary>
    /// Strongly typed telemetry payload.
    ///
    /// This property remains T rather than object, avoiding unnecessary
    /// boxing and preserving compile-time type safety.
    /// </summary>
    public T Payload { get; }

    /// <summary>
    /// UTC timestamp representing when the sensor captured the reading.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; }
}