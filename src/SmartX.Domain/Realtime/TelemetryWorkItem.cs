using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Domain.Realtime;

/// <summary>
/// Immutable acceptance metadata. Arrival, capture and acceptance time are
/// separate: priority dispatch must never pretend to be observation order.
/// </summary>
public abstract class TelemetryWorkItem
{
    private protected TelemetryWorkItem(
        Guid sensorProfileId,
        DeviceIdentifier sourceDeviceIdentifier,
        TelemetryDataType dataType,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset acceptedAtUtc,
        DashboardIssueSeverity severity,
        long arrivalSequence)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new ArgumentException("A registered sensor ID is required.", nameof(sensorProfileId));
        }

        ArgumentNullException.ThrowIfNull(sourceDeviceIdentifier);
        if (acceptedAtUtc == default)
        {
            throw new ArgumentException("An acceptance timestamp is required.", nameof(acceptedAtUtc));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        if (arrivalSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arrivalSequence));
        }

        SensorProfileId = sensorProfileId;
        SourceDeviceIdentifier = sourceDeviceIdentifier;
        DataType = dataType;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        Severity = severity;
        ArrivalSequence = arrivalSequence;
        Lane = severity == DashboardIssueSeverity.Critical
            ? TelemetryProcessingLane.Critical
            : TelemetryProcessingLane.Ordinary;
    }

    public Guid SensorProfileId { get; }
    public DeviceIdentifier SourceDeviceIdentifier { get; }
    public TelemetryDataType DataType { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public DateTimeOffset AcceptedAtUtc { get; }
    public DashboardIssueSeverity Severity { get; }
    public long ArrivalSequence { get; }
    public TelemetryProcessingLane Lane { get; }
}

/// <summary>
/// Retains the exact accepted packet and native payload type. The buffer does
/// not reconstruct readings from capped recent history or convert values to object.
/// </summary>
public sealed class TelemetryWorkItem<T> : TelemetryWorkItem
    where T : struct
{
    public TelemetryWorkItem(
        Guid sensorProfileId,
        TelemetryPacket<T> packet,
        DashboardIssueSeverity severity,
        DateTimeOffset acceptedAtUtc,
        long arrivalSequence)
        : base(
            sensorProfileId,
            (packet ?? throw new ArgumentNullException(nameof(packet))).SourceDeviceIdentifier,
            ResolveDataType(),
            packet.CapturedAtUtc,
            acceptedAtUtc,
            severity,
            arrivalSequence)
    {
        Packet = packet;
    }

    public TelemetryPacket<T> Packet { get; }

    private static TelemetryDataType ResolveDataType()
    {
        if (typeof(T) == typeof(double))
        {
            return TelemetryDataType.Float;
        }

        if (typeof(T) == typeof(int))
        {
            return TelemetryDataType.Integer;
        }

        if (typeof(T) == typeof(bool))
        {
            return TelemetryDataType.Boolean;
        }

        throw new ArgumentException("Processing supports only double, int and bool telemetry.");
    }
}