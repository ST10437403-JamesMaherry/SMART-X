using SmartX.Application.Realtime;
using SmartX.Domain.Realtime;

namespace SmartX.Infrastructure.Realtime;

/// <summary>
/// Synchronized, process-local chronological operational history.
///
/// Each device owns a real SortedDictionary&lt;DateTimeOffset, List&lt;HistoryEntry&gt;&gt;.
/// Its timestamp buckets retain collisions, while a one-based insertion
/// sequence provides deterministic ordering inside each bucket.
///
/// The retention cap is independent of Part 1's recent telemetry limit and
/// P2-03's recent dispatch diagnostics. Oldest occurrence times are removed
/// first, including late-arriving historical readings.
/// </summary>
public sealed class InMemoryRealtimeHistoryStore : IRealtimeHistoryStore
{
    public const int DefaultRetentionCapacityPerDevice = 10_000;
    public const int MaximumRetentionCapacityPerDevice = 100_000;

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, DeviceHistory> _devices = new();
    private readonly int _retentionCapacityPerDevice;

    public InMemoryRealtimeHistoryStore(
        int retentionCapacityPerDevice = DefaultRetentionCapacityPerDevice)
    {
        if (retentionCapacityPerDevice is < 1 or > MaximumRetentionCapacityPerDevice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionCapacityPerDevice),
                $"Retention must be between 1 and {MaximumRetentionCapacityPerDevice} entries per device.");
        }

        _retentionCapacityPerDevice = retentionCapacityPerDevice;
    }

    /// <inheritdoc />
    public bool TryAppend(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Sequence != 0)
        {
            throw new ArgumentException("Only unsequenced source events may be admitted.", nameof(entry));
        }

        lock (_syncRoot)
        {
            if (!_devices.TryGetValue(entry.TargetDeviceId, out DeviceHistory? device))
            {
                device = new DeviceHistory();
                _devices.Add(entry.TargetDeviceId, device);
            }

            // Idempotent callbacks cannot create duplicate history rows.
            // A changed payload under an already-retained identity is an
            // integrity violation, not an overwrite of earlier evidence.
            if (device.ByEventId.TryGetValue(entry.EventId, out HistoryEntry? existing))
            {
                if (!existing.MatchesSource(entry))
                {
                    throw new InvalidOperationException(
                        "A different operational event reused a retained event identity.");
                }

                return false;
            }

            if (device.LastSequence == long.MaxValue || device.TotalRecordedEvents == long.MaxValue)
            {
                throw new InvalidOperationException("Operational history sequence capacity exhausted.");
            }

            long sequence = device.LastSequence + 1;
            HistoryEntry stored = entry.WithSequence(sequence);

            if (!device.ByTime.TryGetValue(stored.OccurredAtUtc, out List<HistoryEntry>? bucket))
            {
                bucket = [];
                device.ByTime.Add(stored.OccurredAtUtc, bucket);
            }

            bucket.Add(stored);

            try
            {
                device.ByEventId.Add(stored.EventId, stored);
            }
            catch
            {
                // Preserve consistent indexes if identity indexing fails.
                bucket.RemoveAt(bucket.Count - 1);
                if (bucket.Count == 0)
                {
                    device.ByTime.Remove(stored.OccurredAtUtc);
                }

                throw;
            }

            device.LastSequence = sequence;
            device.TotalRecordedEvents++;
            device.RetainedEventCount++;

            EvictOldestOccurrences(device);
            return true;
        }
    }

    /// <inheritdoc />
    public RealtimeHistorySnapshot GetSnapshot(
        Guid targetDeviceId,
        int limit = IRealtimeHistoryStore.DefaultQueryLimit,
        HistorySortOrder order = HistorySortOrder.Ascending)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new ArgumentException("A valid target device ID is required.", nameof(targetDeviceId));
        }

        if (limit is < 1 or > IRealtimeHistoryStore.MaximumQueryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"A history query must request between 1 and {IRealtimeHistoryStore.MaximumQueryLimit} entries.");
        }

        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        lock (_syncRoot)
        {
            if (!_devices.TryGetValue(targetDeviceId, out DeviceHistory? device))
            {
                return new RealtimeHistorySnapshot(
                    targetDeviceId,
                    0,
                    0,
                    _retentionCapacityPerDevice,
                    limit,
                    order,
                    Array.AsReadOnly(Array.Empty<HistoryEntry>()));
            }

            // Start at the newest occurrence. Within a timestamp bucket,
            // the last appended event has the greatest stable sequence.
            var newestFirst = new List<HistoryEntry>(Math.Min(limit, device.RetainedEventCount));

            foreach (KeyValuePair<DateTimeOffset, List<HistoryEntry>> timeBucket in device.ByTime.Reverse())
            {
                for (int index = timeBucket.Value.Count - 1; index >= 0; index--)
                {
                    newestFirst.Add(timeBucket.Value[index]);
                    if (newestFirst.Count == limit)
                    {
                        break;
                    }
                }

                if (newestFirst.Count == limit)
                {
                    break;
                }
            }

            if (order == HistorySortOrder.Ascending)
            {
                newestFirst.Reverse();
            }

            return new RealtimeHistorySnapshot(
                targetDeviceId,
                device.TotalRecordedEvents,
                device.RetainedEventCount,
                _retentionCapacityPerDevice,
                limit,
                order,
                Array.AsReadOnly(newestFirst.ToArray()));
        }
    }

    /// <summary>
    /// Evicts the earliest chronological event until capacity is restored.
    /// The oldest bucket is inspected through SortedDictionary enumeration;
    /// no parallel unsorted timeline or telemetry repository is maintained.
    /// </summary>
    private void EvictOldestOccurrences(DeviceHistory device)
    {
        while (device.RetainedEventCount > _retentionCapacityPerDevice)
        {
            KeyValuePair<DateTimeOffset, List<HistoryEntry>> oldest = device.ByTime.First();
            HistoryEntry removed = oldest.Value[0];

            oldest.Value.RemoveAt(0);
            device.ByEventId.Remove(removed.EventId);
            device.RetainedEventCount--;

            if (oldest.Value.Count == 0)
            {
                device.ByTime.Remove(oldest.Key);
            }
        }
    }

    /// <summary>
    /// All mutable collections are private and accessed only under _syncRoot.
    /// Known event identities cover retained events; they are removed when
    /// retention evicts their records to avoid unbounded deduplication memory.
    /// </summary>
    private sealed class DeviceHistory
    {
        public SortedDictionary<DateTimeOffset, List<HistoryEntry>> ByTime { get; } = new();

        public Dictionary<string, HistoryEntry> ByEventId { get; } =
            new(StringComparer.Ordinal);

        public long LastSequence { get; set; }

        public long TotalRecordedEvents { get; set; }

        public int RetainedEventCount { get; set; }
    }
}