using SmartX.Application.Telemetry;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Telemetry;

public sealed class TelemetryContractMapperTests
{
    private static readonly DateTimeOffset CapturedAtUtc =
        new(
            2026,
            9,
            11,
            9,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void ToResponse_WithFloatPacket_PopulatesOnlyFloatValue()
    {
        var sensorId =
            Guid.NewGuid();

        var packet =
            new NumericTelemetryPacket<double>(
                CreateIdentifier(),
                23.75,
                CapturedAtUtc);

        var response =
            TelemetryContractMapper.ToResponse(
                sensorId,
                packet);

        Assert.Equal(
            sensorId,
            response.SensorProfileId);

        Assert.Equal(
            "Float",
            response.TelemetryDataType);

        Assert.Equal(
            23.75,
            response.FloatValue);

        Assert.Null(
            response.IntegerValue);

        Assert.Null(
            response.BooleanValue);
    }

    [Fact]
    public void ToResponse_WithIntegerPacket_PopulatesOnlyIntegerValue()
    {
        var packet =
            new NumericTelemetryPacket<int>(
                CreateIdentifier(),
                4200,
                CapturedAtUtc);

        var response =
            TelemetryContractMapper.ToResponse(
                Guid.NewGuid(),
                packet);

        Assert.Equal(
            "Integer",
            response.TelemetryDataType);

        Assert.Null(
            response.FloatValue);

        Assert.Equal(
            4200,
            response.IntegerValue);

        Assert.Null(
            response.BooleanValue);
    }

    [Fact]
    public void ToResponse_WithBooleanPacket_PreservesFalseValue()
    {
        var packet =
            new TelemetryPacket<bool>(
                CreateIdentifier(),
                false,
                CapturedAtUtc);

        var response =
            TelemetryContractMapper.ToResponse(
                Guid.NewGuid(),
                packet);

        Assert.Equal(
            "Boolean",
            response.TelemetryDataType);

        Assert.Null(
            response.FloatValue);

        Assert.Null(
            response.IntegerValue);

        Assert.False(
            response.BooleanValue);
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:40");
    }
}