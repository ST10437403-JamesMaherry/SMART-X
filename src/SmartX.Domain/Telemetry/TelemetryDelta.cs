using System.Numerics;

using SmartX.Domain.Sensors;

namespace SmartX.Domain.Telemetry;

/// <summary>
/// Represents the strongly typed change between two chronological numeric
/// telemetry readings from the same Smart-X device.
///
/// This is a readonly value type because a telemetry delta is a small,
/// immutable calculation result that may be produced frequently while
/// analysing high-throughput sensor streams.
/// </summary>
/// <typeparam name="T">
/// Numeric telemetry type such as double or int.
/// </typeparam>
public readonly record struct TelemetryDelta<T>
    where T : struct, INumber<T>
{
    internal TelemetryDelta(
        DeviceIdentifier sourceDeviceIdentifier,
        T previousValue,
        T currentValue,
        T delta,
        TimeSpan elapsed)
    {
        SourceDeviceIdentifier = sourceDeviceIdentifier;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        Delta = delta;
        Elapsed = elapsed;
    }

    /// <summary>
    /// Device whose readings were compared.
    /// </summary>
    public DeviceIdentifier SourceDeviceIdentifier { get; }

    /// <summary>
    /// Earlier telemetry value.
    /// </summary>
    public T PreviousValue { get; }

    /// <summary>
    /// Most recent telemetry value.
    /// </summary>
    public T CurrentValue { get; }

    /// <summary>
    /// Signed change calculated as current value minus previous value.
    /// </summary>
    public T Delta { get; }

    /// <summary>
    /// Absolute magnitude of the telemetry change.
    /// </summary>
    public T Magnitude => T.Abs(Delta);

    /// <summary>
    /// Time between the two captured readings.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Indicates that the sensor value increased.
    /// </summary>
    public bool IsIncrease => Delta > T.Zero;

    /// <summary>
    /// Indicates that the sensor value decreased.
    /// </summary>
    public bool IsDecrease => Delta < T.Zero;

    /// <summary>
    /// Indicates that the sensor value did not change.
    /// </summary>
    public bool IsUnchanged => Delta == T.Zero;
}