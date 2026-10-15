using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Infrastructure.Tests.Realtime;

/// <summary>
/// Proves that a real SortedDictionary orders timestamp buckets, retains
/// colliding events, and enforces bounded read/retention semantics without
/// changing the existing Part 1 telemetry repository.
/// </summary>
public sealed class InMemoryRealtimeHistoryStoreTests
{
    private static readonly Guid DeviceA = Guid.Parse("a4e6fd92-adc2-47f2-994a-2f917b1321d4");
    private static readonly Guid DeviceB = Guid.Parse("ca07f45f-d7dd-439d-b746-b0dac0c691a0");
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentifier Source = DeviceIdentifier.FromUniqueIdentifier("history-store-node");

    [Fact]
    public void EmptyDevice_ReturnsDetachedEmptySnapshotAndRetentionMetadata()
    {
        var store = new InMemoryRealtimeHistoryStore();
        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA);

        Assert.Equal(DeviceA, snapshot.TargetDeviceId);
        Assert.Equal(0, snapshot.TotalRecordedEvents);
        Assert.Equal(0, snapshot.RetainedEventCount);
        Assert.Equal(InMemoryRealtimeHistoryStore.DefaultRetentionCapacityPerDevice,
            snapshot.RetentionCapacity);
        Assert.Equal(100, snapshot.Limit);
        Assert.Equal(HistorySortOrder.Ascending, snapshot.Order);
        Assert.Empty(snapshot.Events);
        Assert.True(Assert.IsAssignableFrom<IList<HistoryEntry>>(snapshot.Events).IsReadOnly);
    }

    [Fact]
    public void OutOfOrderArrival_UsesOccurrenceTimeInsteadOfProcessingOrder()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry newest = Telemetry(DeviceA, 1, 30, BaseTime.AddMinutes(3));
        HistoryEntry oldest = Telemetry(DeviceA, 2, 10, BaseTime.AddMinutes(1));
        HistoryEntry middle = Telemetry(DeviceA, 3, 20, BaseTime.AddMinutes(2));

        Assert.True(store.TryAppend(newest));
        Assert.True(store.TryAppend(oldest));
        Assert.True(store.TryAppend(middle));

        RealtimeHistorySnapshot timeline = store.GetSnapshot(DeviceA);
        Assert.Equal(new[] { oldest.EventId, middle.EventId, newest.EventId },
            timeline.Events.Select(entry => entry.EventId));
        Assert.Equal(new long[] { 2, 3, 1 }, timeline.Events.Select(entry => entry.Sequence));
        Assert.Equal(3, timeline.RetainedEventCount);
        Assert.Equal(3, timeline.TotalRecordedEvents);
    }

    [Fact]
    public void EqualTimestamps_RetainTelemetryOriginalCommandAndUndoInStableOrder()
    {
        var store = new InMemoryRealtimeHistoryStore();
        CommandRecord command = new(Guid.NewGuid(), DeviceA, RelayCommandType.RelayOn,
            BaseTime, RelayState.Off, RelayState.On);
        CommandRecord reversal = new(Guid.NewGuid(), DeviceA, RelayCommandType.RelayOff,
            BaseTime, RelayState.On, RelayState.Off, command.Id);
        HistoryEntry first = HistoryEntry.FromCommand(command);
        HistoryEntry second = Telemetry(DeviceA, 1, 42, BaseTime);
        HistoryEntry third = HistoryEntry.FromCommand(reversal);

        store.TryAppend(first);
        store.TryAppend(second);
        store.TryAppend(third);

        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA);
        Assert.Equal(3, snapshot.Events.Count);
        Assert.All(snapshot.Events, entry => Assert.Equal(BaseTime, entry.OccurredAtUtc));
        Assert.Equal(new long[] { 1, 2, 3 }, snapshot.Events.Select(entry => entry.Sequence));
        Assert.Equal(new[]
        {
            HistoryEventKind.RelayCommand,
            HistoryEventKind.TelemetryProcessed,
            HistoryEventKind.RelayUndo
        }, snapshot.Events.Select(entry => entry.Kind));
        Assert.Equal(command.Id, snapshot.Events[^1].Command!.ReversesCommandId);
    }

    [Fact]
    public void UtcOffsets_CompareAsSameInstantAndShareTimestampBucket()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry first = Telemetry(DeviceA, 1, 1, BaseTime.ToOffset(TimeSpan.FromHours(2)));
        HistoryEntry second = Telemetry(DeviceA, 2, 2, BaseTime.ToOffset(TimeSpan.FromHours(-3)));

        store.TryAppend(first);
        store.TryAppend(second);

        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA);
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(new long[] { 1, 2 }, snapshot.Events.Select(entry => entry.Sequence));
        Assert.All(snapshot.Events, entry =>
        {
            Assert.Equal(BaseTime, entry.OccurredAtUtc);
            Assert.Equal(TimeSpan.Zero, entry.OccurredAtUtc.Offset);
        });
    }

    [Fact]
    public void DuplicateCallback_SameEventIdAndPayload_IsNotRecordedTwice()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry first = Telemetry(DeviceA, 5, 12, BaseTime);
        HistoryEntry repeated = Telemetry(DeviceA, 5, 12, BaseTime, BaseTime.AddMinutes(4));

        Assert.True(store.TryAppend(first));
        Assert.False(store.TryAppend(first));
        Assert.False(store.TryAppend(repeated));

        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA);
        Assert.Single(snapshot.Events);
        Assert.Equal(1, snapshot.TotalRecordedEvents);
        Assert.Equal(1, snapshot.RetainedEventCount);
        Assert.Equal(1, snapshot.Events[0].Sequence);
    }

    [Fact]
    public void ConflictingEventIdentity_RejectsDifferentPayloadWithoutMutation()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry original = Telemetry(DeviceA, 6, 10, BaseTime);
        HistoryEntry conflicting = Telemetry(DeviceA, 6, 20, BaseTime);

        Assert.True(store.TryAppend(original));
        Assert.Throws<InvalidOperationException>(() => store.TryAppend(conflicting));

        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA);
        Assert.Equal(1, snapshot.TotalRecordedEvents);
        Assert.Equal(1, snapshot.RetainedEventCount);
        Assert.Equal(original.EventId, Assert.Single(snapshot.Events).EventId);
    }

    [Fact]
    public void Retention_EvictsOldestOccurrenceEvenForLateHistoricalAdmission()
    {
        var store = new InMemoryRealtimeHistoryStore(retentionCapacityPerDevice: 3);
        HistoryEntry middle = Telemetry(DeviceA, 1, 2, BaseTime.AddMinutes(2));
        HistoryEntry earliest = Telemetry(DeviceA, 2, 1, BaseTime.AddMinutes(1));
        HistoryEntry later = Telemetry(DeviceA, 3, 3, BaseTime.AddMinutes(3));
        HistoryEntry newest = Telemetry(DeviceA, 4, 4, BaseTime.AddMinutes(4));

        store.TryAppend(middle);
        store.TryAppend(earliest);
        store.TryAppend(later);
        store.TryAppend(newest);

        RealtimeHistorySnapshot afterEviction = store.GetSnapshot(DeviceA);
        Assert.Equal(new[] { middle.EventId, later.EventId, newest.EventId },
            afterEviction.Events.Select(entry => entry.EventId));
        Assert.Equal(3, afterEviction.RetainedEventCount);
        Assert.Equal(4, afterEviction.TotalRecordedEvents);

        // An even older accepted event cannot displace newer occurrences.
        HistoryEntry late = Telemetry(DeviceA, 5, 0, BaseTime.AddDays(-1));
        Assert.True(store.TryAppend(late));

        RealtimeHistorySnapshot final = store.GetSnapshot(DeviceA);
        Assert.Equal(new[] { middle.EventId, later.EventId, newest.EventId },
            final.Events.Select(entry => entry.EventId));
        Assert.Equal(3, final.RetainedEventCount);
        Assert.Equal(5, final.TotalRecordedEvents);
        Assert.Equal(new long[] { 1, 3, 4 }, final.Events.Select(entry => entry.Sequence));
    }

    [Fact]
    public void BoundedQuery_SelectsNewestEventsThenAppliesRequestedOrder()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry[] events = Enumerable.Range(0, 7)
            .Select(index => Telemetry(DeviceA, index + 1, index, BaseTime.AddMinutes(index)))
            .ToArray();

        foreach (HistoryEntry entry in events)
        {
            store.TryAppend(entry);
        }

        RealtimeHistorySnapshot ascending = store.GetSnapshot(DeviceA, 3);
        RealtimeHistorySnapshot descending = store.GetSnapshot(DeviceA, 3, HistorySortOrder.Descending);

        Assert.Equal(events.Skip(4).Select(entry => entry.EventId),
            ascending.Events.Select(entry => entry.EventId));
        Assert.Equal(events.Skip(4).Reverse().Select(entry => entry.EventId),
            descending.Events.Select(entry => entry.EventId));
        Assert.Equal(7, ascending.TotalRecordedEvents);
        Assert.Equal(7, descending.RetainedEventCount);
        Assert.Equal(3, ascending.Limit);
        Assert.Equal(HistorySortOrder.Descending, descending.Order);
    }

    [Fact]
    public void MultipleDevices_HaveIndependentIdentitiesSequencesAndRetention()
    {
        var store = new InMemoryRealtimeHistoryStore(retentionCapacityPerDevice: 2);
        HistoryEntry a1 = Telemetry(DeviceA, 1, 10, BaseTime);
        HistoryEntry b1 = Telemetry(DeviceB, 1, 20, BaseTime);
        HistoryEntry a2 = Telemetry(DeviceA, 2, 11, BaseTime.AddMinutes(1));
        HistoryEntry a3 = Telemetry(DeviceA, 3, 12, BaseTime.AddMinutes(2));

        Assert.True(store.TryAppend(a1));
        Assert.True(store.TryAppend(b1));
        Assert.True(store.TryAppend(a2));
        Assert.True(store.TryAppend(a3));

        RealtimeHistorySnapshot first = store.GetSnapshot(DeviceA);
        RealtimeHistorySnapshot second = store.GetSnapshot(DeviceB);

        Assert.Equal(new[] { a2.EventId, a3.EventId },
            first.Events.Select(entry => entry.EventId));
        Assert.Equal(b1.EventId, Assert.Single(second.Events).EventId);
        Assert.Equal(3, first.TotalRecordedEvents);
        Assert.Equal(1, second.TotalRecordedEvents);
        Assert.Equal(1, second.Events[0].Sequence);
    }

    [Fact]
    public void Snapshot_IsDetachedReadOnlyAndDoesNotChangeAfterLaterInsert()
    {
        var store = new InMemoryRealtimeHistoryStore();
        store.TryAppend(Telemetry(DeviceA, 1, 1, BaseTime));
        RealtimeHistorySnapshot previous = store.GetSnapshot(DeviceA);

        store.TryAppend(Telemetry(DeviceA, 2, 2, BaseTime.AddMinutes(1)));
        RealtimeHistorySnapshot current = store.GetSnapshot(DeviceA);

        Assert.Equal(1, previous.RetainedEventCount);
        Assert.Single(previous.Events);
        Assert.Equal(2, current.RetainedEventCount);
        Assert.Equal(2, current.Events.Count);

        var readOnly = Assert.IsAssignableFrom<IList<HistoryEntry>>(previous.Events);
        Assert.True(readOnly.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => readOnly[0] = current.Events[1]);
    }

    [Fact]
    public void MoreThanPartOneRecentLimit_AllEventsRemainInIndependentTimeline()
    {
        var store = new InMemoryRealtimeHistoryStore();

        for (int index = 0; index < 151; index++)
        {
            Assert.True(store.TryAppend(Telemetry(
                DeviceA, index + 1, index, BaseTime.AddSeconds(index))));
        }

        RealtimeHistorySnapshot defaultView = store.GetSnapshot(DeviceA);
        RealtimeHistorySnapshot fullView = store.GetSnapshot(DeviceA, 200);

        Assert.Equal(151, fullView.TotalRecordedEvents);
        Assert.Equal(151, fullView.RetainedEventCount);
        Assert.Equal(151, fullView.Events.Count);
        Assert.Equal(100, defaultView.Events.Count);
        Assert.Equal(52, defaultView.Events[0].Sequence);
        Assert.Equal(151, defaultView.Events[^1].Sequence);
        Assert.IsType<TelemetryWorkItem<int>>(fullView.Events[^1].TelemetryDispatch!.WorkItem);
    }

    [Fact]
    public void ConcurrentAdmissions_AreAccountedOnceAndEqualTimesHaveUniqueStableSequence()
    {
        var store = new InMemoryRealtimeHistoryStore();
        HistoryEntry[] entries = Enumerable.Range(0, 128)
            .Select(index => Telemetry(DeviceA, index + 1, index, BaseTime))
            .ToArray();

        Parallel.ForEach(entries, entry =>
        {
            store.TryAppend(entry);
            store.TryAppend(entry); // Simulates a repeated callback.
        });

        RealtimeHistorySnapshot snapshot = store.GetSnapshot(DeviceA, 200);
        Assert.Equal(128, snapshot.TotalRecordedEvents);
        Assert.Equal(128, snapshot.RetainedEventCount);
        Assert.Equal(128, snapshot.Events.Count);
        Assert.Equal(128, snapshot.Events.Select(entry => entry.EventId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 128).Select(i => (long)i),
            snapshot.Events.Select(entry => entry.Sequence));
    }

    [Fact]
    public void InvalidInputs_AreRejectedBeforeStoringOrQuerying()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryRealtimeHistoryStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryRealtimeHistoryStore(InMemoryRealtimeHistoryStore.MaximumRetentionCapacityPerDevice + 1));

        var store = new InMemoryRealtimeHistoryStore();
        Assert.Throws<ArgumentNullException>(() => store.TryAppend(null!));
        Assert.Throws<ArgumentException>(() => store.GetSnapshot(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetSnapshot(DeviceA, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetSnapshot(DeviceA, 501));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.GetSnapshot(DeviceA, 1, (HistorySortOrder)999));

        var first = Telemetry(DeviceA, 1, 1, BaseTime);
        Assert.Throws<ArgumentException>(() => store.TryAppend(first.WithSequence(1)));
        Assert.Empty(store.GetSnapshot(DeviceA).Events);
    }

    private static HistoryEntry Telemetry(
        Guid deviceId, long arrivalSequence, int value,
        DateTimeOffset occurredAtUtc, DateTimeOffset? processedAtUtc = null)
    {
        var packet = new TelemetryPacket<int>(Source, value, occurredAtUtc);
        var item = new TelemetryWorkItem<int>(deviceId, packet,
            DashboardIssueSeverity.Information, BaseTime, arrivalSequence);
        var dispatch = new TelemetryDispatch(item, arrivalSequence);
        return HistoryEntry.FromProcessedTelemetry(dispatch, processedAtUtc ?? BaseTime.AddMinutes(5));
    }
}