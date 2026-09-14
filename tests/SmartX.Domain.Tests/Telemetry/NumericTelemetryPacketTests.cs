using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

public sealed class NumericTelemetryPacketTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            10,
            10,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void Subtraction_WhenTemperatureRises_ReturnsTypedPositiveDelta()
    {
        var identifier = CreateIdentifier();

        var previous =
            new NumericTelemetryPacket<double>(
                identifier,
                22.4,
                BaseTime);

        var current =
            new NumericTelemetryPacket<double>(
                identifier,
                27.1,
                BaseTime.AddMinutes(5));

        // This is the structural purpose of the overloaded operator:
        // Smart-X can directly calculate how much a reading changed.
        var delta = current - previous;

        Assert.Equal(
            4.7,
            delta.Delta,
            precision: 10);

        Assert.Equal(
            4.7,
            delta.Magnitude,
            precision: 10);

        Assert.Equal(
            TimeSpan.FromMinutes(5),
            delta.Elapsed);

        Assert.True(delta.IsIncrease);
        Assert.False(delta.IsDecrease);
        Assert.False(delta.IsUnchanged);
    }

    [Fact]
    public void Subtraction_WhenPowerReadingDrops_ReturnsTypedIntegerDelta()
    {
        var identifier = CreateIdentifier();

        var previous =
            new NumericTelemetryPacket<int>(
                identifier,
                4800,
                BaseTime);

        var current =
            new NumericTelemetryPacket<int>(
                identifier,
                4100,
                BaseTime.AddSeconds(30));

        var delta = current - previous;

        Assert.Equal(-700, delta.Delta);
        Assert.Equal(700, delta.Magnitude);

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            delta.Elapsed);

        Assert.True(delta.IsDecrease);
        Assert.False(delta.IsIncrease);
        Assert.False(delta.IsUnchanged);
    }

    [Fact]
    public void Subtraction_WhenReadingDoesNotChange_ReportsUnchanged()
    {
        var identifier = CreateIdentifier();

        var previous =
            new NumericTelemetryPacket<double>(
                identifier,
                18.5,
                BaseTime);

        var current =
            new NumericTelemetryPacket<double>(
                identifier,
                18.5,
                BaseTime.AddMinutes(1));

        var delta = current - previous;

        Assert.Equal(0.0, delta.Delta);
        Assert.Equal(0.0, delta.Magnitude);

        Assert.True(delta.IsUnchanged);
        Assert.False(delta.IsIncrease);
        Assert.False(delta.IsDecrease);
    }

    [Fact]
    public void Subtraction_PreservesNumericGenericType()
    {
        var identifier = CreateIdentifier();

        var previous =
            new NumericTelemetryPacket<int>(
                identifier,
                100,
                BaseTime);

        var current =
            new NumericTelemetryPacket<int>(
                identifier,
                125,
                BaseTime.AddSeconds(10));

        TelemetryDelta<int> delta = current - previous;

        // No cast from object is required. The delta remains an int.
        int change = delta.Delta;

        Assert.Equal(25, change);
    }

    [Fact]
    public void Subtraction_FromDifferentDevices_ThrowsMeaningfulException()
    {
        var firstIdentifier =
            DeviceIdentifier.FromMacAddress(
                "AA:BB:CC:DD:EE:FF");

        var secondIdentifier =
            DeviceIdentifier.FromMacAddress(
                "11:22:33:44:55:66");

        var previous =
            new NumericTelemetryPacket<double>(
                firstIdentifier,
                22.4,
                BaseTime);

        var current =
            new NumericTelemetryPacket<double>(
                secondIdentifier,
                27.1,
                BaseTime.AddMinutes(5));

        var exception =
            Assert.Throws<InvalidTelemetryOperationException>(
                () =>
                {
                    _ = current - previous;
                });

        Assert.Contains(
            "same source device",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Subtraction_InReverseChronologicalOrder_ThrowsException()
    {
        var identifier = CreateIdentifier();

        var newer =
            new NumericTelemetryPacket<double>(
                identifier,
                27.1,
                BaseTime.AddMinutes(5));

        var older =
            new NumericTelemetryPacket<double>(
                identifier,
                22.4,
                BaseTime);

        var exception =
            Assert.Throws<InvalidTelemetryOperationException>(
                () =>
                {
                    // The left-hand operand represents the current reading.
                    // Supplying an older packet as "current" is therefore
                    // rejected by the domain model.
                    _ = older - newer;
                });

        Assert.Contains(
            "cannot be older",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_WithNonFiniteFloatingPointValue_ThrowsException(
        double invalidValue)
    {
        Assert.Throws<InvalidTelemetryPacketException>(
            () => new NumericTelemetryPacket<double>(
                CreateIdentifier(),
                invalidValue,
                BaseTime));
    }

    [Fact]
    public void Constructor_WithFiniteDouble_AllowsValidSensorReading()
    {
        var packet =
            new NumericTelemetryPacket<double>(
                CreateIdentifier(),
                24.75,
                BaseTime);

        Assert.Equal(24.75, packet.Payload);
    }

    [Fact]
    public void Constructor_WithInteger_AllowsNumericSensorReading()
    {
        var packet =
            new NumericTelemetryPacket<int>(
                CreateIdentifier(),
                4200,
                BaseTime);

        Assert.Equal(4200, packet.Payload);
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:FF");
    }
}