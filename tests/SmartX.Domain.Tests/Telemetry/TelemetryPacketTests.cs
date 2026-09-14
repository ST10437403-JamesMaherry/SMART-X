using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

public sealed class TelemetryPacketTests
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
    public void Constructor_WithDoublePayload_PreservesStrongType()
    {
        var packet = new TelemetryPacket<double>(
            CreateIdentifier(),
            24.75,
            BaseTime);

        // This assignment compiles without a cast because Payload is
        // genuinely a double rather than an object containing a double.
        double reading = packet.Payload;

        Assert.Equal(24.75, reading);
    }

    [Fact]
    public void Constructor_WithIntegerPayload_PreservesStrongType()
    {
        var packet = new TelemetryPacket<int>(
            CreateIdentifier(),
            4200,
            BaseTime);

        int reading = packet.Payload;

        Assert.Equal(4200, reading);
    }

    [Fact]
    public void Constructor_WithBooleanPayload_PreservesStrongType()
    {
        var packet = new TelemetryPacket<bool>(
            CreateIdentifier(),
            true,
            BaseTime);

        bool valveOpen = packet.Payload;

        Assert.True(valveOpen);
    }

    [Fact]
    public void PayloadProperty_UsesClosedGenericTypeInsteadOfObject()
    {
        var payloadProperty =
            typeof(TelemetryPacket<double>)
                .GetProperty(nameof(TelemetryPacket<double>.Payload));

        Assert.NotNull(payloadProperty);

        // Explicitly proves the public telemetry model exposes a strongly
        // typed double rather than weakening the payload to object.
        Assert.Equal(typeof(double), payloadProperty.PropertyType);
        Assert.NotEqual(typeof(object), payloadProperty.PropertyType);
    }

    [Fact]
    public void Constructor_WithMissingDeviceIdentifier_ThrowsException()
    {
        Assert.Throws<InvalidTelemetryPacketException>(
            () => new TelemetryPacket<double>(
                null,
                24.75,
                BaseTime));
    }

    [Fact]
    public void Constructor_WithDefaultTimestamp_ThrowsException()
    {
        Assert.Throws<InvalidTelemetryPacketException>(
            () => new TelemetryPacket<double>(
                CreateIdentifier(),
                24.75,
                default));
    }

    [Fact]
    public void Constructor_WithOffsetTimestamp_NormalisesTimestampToUtc()
    {
        var southAfricanTime =
            new DateTimeOffset(
                2026,
                9,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(2));

        var packet = new TelemetryPacket<double>(
            CreateIdentifier(),
            24.75,
            southAfricanTime);

        Assert.Equal(TimeSpan.Zero, packet.CapturedAtUtc.Offset);

        Assert.Equal(
            new DateTimeOffset(
                2026,
                9,
                10,
                10,
                0,
                0,
                TimeSpan.Zero),
            packet.CapturedAtUtc);
    }

    [Fact]
    public void Constructor_WithZeroInteger_AllowsLegitimateZeroReading()
    {
        var packet = new TelemetryPacket<int>(
            CreateIdentifier(),
            0,
            BaseTime);

        Assert.Equal(0, packet.Payload);
    }

    [Fact]
    public void Constructor_WithFalseBoolean_AllowsLegitimateFalseState()
    {
        var packet = new TelemetryPacket<bool>(
            CreateIdentifier(),
            false,
            BaseTime);

        Assert.False(packet.Payload);
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:FF");
    }
}