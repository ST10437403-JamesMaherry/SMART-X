using SmartX.Application.Realtime;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Telemetry;

namespace SmartX.Infrastructure.Realtime;

/// <summary>
/// Uses the actual rubric collections, protected by one short lock. Neither
/// packet processing nor asynchronous waiting occurs while holding the lock.
/// Work is process-local, matching the existing in-memory telemetry repository.
/// </summary>
public sealed class InMemoryTelemetryWorkBuffer : ITelemetryWorkBuffer
{
    private readonly object _syncRoot = new();
    private readonly Queue<TelemetryWorkItem> _ordinary = new();
    private readonly PriorityQueue<TelemetryWorkItem, TelemetryWorkPriority> _critical =
        new(TelemetryWorkPriorityComparer.Instance);

    private TaskCompletionSource<bool> _changed = CreateSignal();
    private long _acceptedCount;
    private long _dispatchedCount;
    private bool _ordinaryDispatchPaused;

    public TelemetryWorkItem<T> Enqueue<T>(
        Guid sensorProfileId,
        TelemetryPacket<T> packet,
        DashboardIssueSeverity severity,
        DateTimeOffset acceptedAtUtc)
        where T : struct
    {
        lock (_syncRoot)
        {
            // Validate before publishing the sequence or touching either lane.
            var nextSequence = checked(_acceptedCount + 1);
            var item = new TelemetryWorkItem<T>(
                sensorProfileId, packet, severity, acceptedAtUtc, nextSequence);

            if (item.Lane == TelemetryProcessingLane.Critical)
            {
                _critical.Enqueue(item, new TelemetryWorkPriority(severity, nextSequence));
            }
            else
            {
                _ordinary.Enqueue(item);
            }

            _acceptedCount = nextSequence;
            SignalChange();
            return item;
        }
    }

    public async ValueTask<TelemetryDispatch> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task change;
            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_critical.Count > 0 || (!_ordinaryDispatchPaused && _ordinary.Count > 0))
                {
                    // Increment only after determining there is dispatchable work.
                    var nextSequence = checked(_dispatchedCount + 1);
                    var item = _critical.Count > 0
                        ? _critical.Dequeue()
                        : _ordinary.Dequeue();

                    _dispatchedCount = nextSequence;
                    return new TelemetryDispatch(item, nextSequence);
                }

                // Capture the signal under the same lock as the empty check.
                // This prevents an admission between checking and subscribing
                // from leaving the worker asleep despite available work.
                change = _changed.Task;
            }

            // Cancellation affects this wait only, never the shared signal.
            await change.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void SetOrdinaryDispatchPaused(bool paused)
    {
        lock (_syncRoot)
        {
            if (_ordinaryDispatchPaused == paused)
            {
                return;
            }

            _ordinaryDispatchPaused = paused;
            SignalChange();
        }
    }

    public TelemetryWorkBufferSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            // Copy both lanes. UnorderedItems is heap order, so only the
            // diagnostic copy is sorted using the real dispatch comparer.
            var ordinary = Array.AsReadOnly(_ordinary.ToArray());
            var critical = Array.AsReadOnly(_critical.UnorderedItems
                .OrderBy(entry => entry.Priority, TelemetryWorkPriorityComparer.Instance)
                .Select(entry => entry.Element)
                .ToArray());

            return new TelemetryWorkBufferSnapshot(
                _acceptedCount, _dispatchedCount, _ordinaryDispatchPaused, ordinary, critical);
        }
    }

    private static TaskCompletionSource<bool> CreateSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Called under the lock; asynchronous continuations cannot run inline here.</summary>
    private void SignalChange()
    {
        var previous = _changed;
        _changed = CreateSignal();
        previous.TrySetResult(true);
    }
}