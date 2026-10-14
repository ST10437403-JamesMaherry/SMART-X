using SmartX.Application.Realtime;
using SmartX.Domain.Realtime;

namespace SmartX.Infrastructure.Realtime;

/// <summary>
/// Short synchronous transitions protect metrics and operational observations.
/// Only dispatch evidence is capped; failed/interrupted work retains its full
/// typed packet until the process ends, and pending work stays in the buffer.
/// </summary>
public sealed class InMemoryTelemetryProcessingStore(ITelemetryWorkBuffer buffer)
    : ITelemetryProcessingStore
{
    public const int DispatchEvidenceLimit = 100;
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, TelemetryDeviceProcessingState> _devices = new();
    private readonly List<TelemetryProcessingRecord> _recent = [];
    private TelemetryWorkerStatus _status = TelemetryWorkerStatus.Starting;
    private TelemetryDispatch? _current;
    private long _processedCount;
    private string? _failure;

    public void MarkWaiting()
    {
        lock (_syncRoot)
        {
            if (_current is not null || _status is TelemetryWorkerStatus.Faulted or TelemetryWorkerStatus.Stopped)
                throw new InvalidOperationException("The worker cannot wait with retained work or after stopping.");
            _status = TelemetryWorkerStatus.Waiting;
        }
    }

    public void Begin(TelemetryDispatch dispatch, DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_syncRoot)
        {
            if (_current is not null || _status is TelemetryWorkerStatus.Faulted or TelemetryWorkerStatus.Stopped)
                throw new InvalidOperationException("Only one dispatch may execute at a time.");
            _current = dispatch;
            _status = TelemetryWorkerStatus.Processing;
            _recent.Add(new TelemetryProcessingRecord(dispatch, startedAtUtc.ToUniversalTime(),
                null, TelemetryDispatchOutcome.Processing, false));
            if (_recent.Count > DispatchEvidenceLimit) _recent.RemoveAt(0);
        }
    }

    public void Complete(TelemetryDispatch dispatch, DateTimeOffset processedAtUtc)
    {
        lock (_syncRoot)
        {
            if (_status != TelemetryWorkerStatus.Processing || !ReferenceEquals(_current, dispatch))
                throw new InvalidOperationException("Only the active dispatch can complete.");

            var item = dispatch.WorkItem;
            _devices.TryGetValue(item.SensorProfileId, out var previous);
            // Capture time defines observation recency. Arrival sequence breaks
            // equal-timestamp ties deterministically, independent of lane order.
            var newer = previous is null || item.CapturedAtUtc > previous.LatestObservation.CapturedAtUtc ||
                (item.CapturedAtUtc == previous.LatestObservation.CapturedAtUtc &&
                 item.ArrivalSequence > previous.LatestObservation.ArrivalSequence);
            var utc = processedAtUtc.ToUniversalTime();
            _devices[item.SensorProfileId] = new TelemetryDeviceProcessingState(
                newer ? item : previous!.LatestObservation, utc);
            _processedCount++;
            _recent[^1] = _recent[^1] with
            {
                FinishedAtUtc = utc,
                Outcome = TelemetryDispatchOutcome.Processed,
                UpdatedLatestObservation = newer
            };
            _current = null;
            _status = TelemetryWorkerStatus.Waiting;
        }
    }

    public void MarkFaulted(DateTimeOffset failedAtUtc)
    {
        lock (_syncRoot)
        {
            _status = TelemetryWorkerStatus.Faulted;
            // Keep exception details in server logs, not in a public response.
            _failure = "Telemetry processing failed. Dispatch is halted; uncompleted work is retained in memory. Check server logs.";
            FinishRetained(TelemetryDispatchOutcome.Failed, failedAtUtc);
        }
    }

    public void MarkStopped(DateTimeOffset stoppedAtUtc)
    {
        lock (_syncRoot)
        {
            if (_status == TelemetryWorkerStatus.Faulted) return;
            _status = TelemetryWorkerStatus.Stopped;
            FinishRetained(TelemetryDispatchOutcome.Interrupted, stoppedAtUtc);
        }
    }

    public TelemetryDeviceProcessingState? GetDeviceState(Guid sensorProfileId)
    {
        lock (_syncRoot) return _devices.GetValueOrDefault(sensorProfileId);
    }

    public TelemetryProcessingSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new TelemetryProcessingSnapshot(buffer.GetSnapshot(), _processedCount,
                _status, _current, _failure, Array.AsReadOnly(_recent.ToArray()));
        }
    }

    private void FinishRetained(TelemetryDispatchOutcome outcome, DateTimeOffset atUtc)
    {
        if (_current is not null)
            _recent[^1] = _recent[^1] with { FinishedAtUtc = atUtc.ToUniversalTime(), Outcome = outcome };
    }
}