using System.Numerics;

using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

namespace SmartX.Domain.Telemetry;

/// <summary>
/// Specialised telemetry packet for numeric sensor readings.
///
/// This type extends the generic TelemetryPacket<T> model with arithmetic
/// behaviour only when T supports .NET generic mathematics. This preserves
/// support for non-numeric telemetry, such as bool values, without using
/// dynamic dispatch, object conversion or boxing.
/// </summary>
/// <typeparam name="T">
/// Numeric telemetry value such as double or int.
/// </typeparam>
public sealed class NumericTelemetryPacket<T> : TelemetryPacket<T>
    where T : struct, INumber<T>
{
    /// <summary>
    /// Creates a numeric telemetry packet.
    /// </summary>
    public NumericTelemetryPacket(
        DeviceIdentifier? sourceDeviceIdentifier,
        T payload,
        DateTimeOffset capturedAtUtc)
        : base(
            sourceDeviceIdentifier,
            ValidatePayload(payload),
            capturedAtUtc)
    {
    }

    /// <summary>
    /// Calculates the strongly typed change between a current reading and
    /// an earlier reading from the same physical sensor.
    ///
    /// Usage:
    /// TelemetryDelta&lt;double&gt; delta = current - previous;
    ///
    /// This gives the overloaded operator a direct Smart-X purpose:
    /// detecting telemetry spikes, drops and other rate-of-change behaviour.
    /// </summary>
    /// <param name="current">
    /// Most recent telemetry packet.
    /// </param>
    /// <param name="previous">
    /// Earlier telemetry packet.
    /// </param>
    /// <returns>
    /// A strongly typed telemetry delta containing the signed change,
    /// magnitude and elapsed time.
    /// </returns>
    /// <exception cref="InvalidTelemetryOperationException">
    /// Thrown when packets originate from different devices or are supplied
    /// in reverse chronological order.
    /// </exception>
    public static TelemetryDelta<T> operator -(
        NumericTelemetryPacket<T> current,
        NumericTelemetryPacket<T> previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        if (current.SourceDeviceIdentifier !=
            previous.SourceDeviceIdentifier)
        {
            throw new InvalidTelemetryOperationException(
                "A telemetry delta can only be calculated between " +
                "readings from the same source device.");
        }

        if (current.CapturedAtUtc < previous.CapturedAtUtc)
        {
            throw new InvalidTelemetryOperationException(
                "The current telemetry packet cannot be older than " +
                "the previous telemetry packet.");
        }

        var delta = current.Payload - previous.Payload;

        return new TelemetryDelta<T>(
            current.SourceDeviceIdentifier,
            previous.Payload,
            current.Payload,
            delta,
            current.CapturedAtUtc - previous.CapturedAtUtc);
    }

    private static T ValidatePayload(T payload)
    {
        // INumber<T> exposes generic numerical classification methods.
        // Integer values are always finite, while floating-point values such
        // as NaN and positive/negative infinity are rejected as unusable
        // physical sensor readings.
        if (!T.IsFinite(payload))
        {
            throw new InvalidTelemetryPacketException(
                "Numeric telemetry must contain a finite value.");
        }

        return payload;
    }
}