using SmartX.Application.Telemetry;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

public static class TelemetryProcessingMapper
{
    public static TelemetryReadingResponse ToReading(TelemetryWorkItem item) => item switch
    {
        TelemetryWorkItem<double> floating => TelemetryContractMapper.ToResponse(item.SensorProfileId, floating.Packet),
        TelemetryWorkItem<int> integer => TelemetryContractMapper.ToResponse(item.SensorProfileId, integer.Packet),
        TelemetryWorkItem<bool> boolean => TelemetryContractMapper.ToResponse(item.SensorProfileId, boolean.Packet),
        _ => throw new InvalidOperationException("Unsupported accepted telemetry type.")
    };

    public static TelemetryProcessingResponse ToResponse(TelemetryProcessingSnapshot snapshot)
    {
        var buffer = snapshot.Buffer;
        var recent = snapshot.RecentDispatches.Select(ToDispatch).ToArray();
        var current = snapshot.CurrentDispatch is null ? null :
            recent.Single(record => record.DequeueSequence == snapshot.CurrentDispatch.DequeueSequence);
        return new TelemetryProcessingResponse(buffer.AcceptedCount, buffer.DispatchedCount,
            snapshot.ProcessedCount, buffer.OrdinaryPendingCount, buffer.CriticalPendingCount,
            buffer.PendingCount, snapshot.OutstandingDispatchedCount, buffer.OrdinaryDispatchPaused,
            snapshot.WorkerStatus.ToString(), snapshot.Failure, current, Array.AsReadOnly(recent));
    }

    private static TelemetryDispatchResponse ToDispatch(TelemetryProcessingRecord record)
    {
        var item = record.Dispatch.WorkItem;
        return new TelemetryDispatchResponse(item.ArrivalSequence, record.Dispatch.DequeueSequence,
            item.Lane.ToString(), item.Severity.ToString(), ToReading(item), item.AcceptedAtUtc,
            record.StartedAtUtc, record.FinishedAtUtc, record.Outcome.ToString(), record.UpdatedLatestObservation);
    }
}