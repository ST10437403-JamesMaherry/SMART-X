using SmartX.Contracts.Telemetry;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Telemetry;

/// <summary>
/// Maps strongly typed Domain telemetry packets into API-safe response
/// contracts.
///
/// Separate overloads retain the payload's compile-time type throughout the
/// mapping process instead of converting values through object or dynamic.
/// </summary>
public static class TelemetryContractMapper
{
    /// <summary>
    /// Maps floating-point telemetry.
    /// </summary>
    public static TelemetryReadingResponse ToResponse(
        Guid sensorProfileId,
        TelemetryPacket<double> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new TelemetryReadingResponse(
            sensorProfileId,
            packet.SourceDeviceIdentifier.Value,
            "Float",
            packet.Payload,
            null,
            null,
            packet.CapturedAtUtc);
    }

    /// <summary>
    /// Maps integer telemetry.
    /// </summary>
    public static TelemetryReadingResponse ToResponse(
        Guid sensorProfileId,
        TelemetryPacket<int> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new TelemetryReadingResponse(
            sensorProfileId,
            packet.SourceDeviceIdentifier.Value,
            "Integer",
            null,
            packet.Payload,
            null,
            packet.CapturedAtUtc);
    }

    /// <summary>
    /// Maps boolean telemetry.
    /// </summary>
    public static TelemetryReadingResponse ToResponse(
        Guid sensorProfileId,
        TelemetryPacket<bool> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new TelemetryReadingResponse(
            sensorProfileId,
            packet.SourceDeviceIdentifier.Value,
            "Boolean",
            null,
            null,
            packet.Payload,
            packet.CapturedAtUtc);
    }
}