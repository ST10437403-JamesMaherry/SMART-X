using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Infrastructure.Tests.Realtime;

/// <summary>
/// Proves actual dequeue order, native packet retention and concurrent accounting.
/// Pause and cancellation provide deterministic coordination without artificial sleeps.
/// </summary>
public sealed class TelemetryWorkBufferTests
{
    private static readonly Guid SensorId = Guid.Parse("55884436-88ad-421a-82c7-b90803510941");
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OrdinaryPackets_DequeueInArrivalOrder_EvenWhenCaptureTimesDiffer()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var first = Admit(buffer, 10, DashboardIssueSeverity.Warning, BaseTime.AddHours(1));
        var second = Admit(buffer, 20, DashboardIssueSeverity.Information, BaseTime.AddHours(-1));

        var a = await buffer.DequeueAsync();
        var b = await buffer.DequeueAsync();

        Assert.Same(first, a.WorkItem);
        Assert.Same(second, b.WorkItem);
        Assert.Equal(1L, a.DequeueSequence);
        Assert.Equal(2L, b.DequeueSequence);
        Assert.Equal(TelemetryProcessingLane.Ordinary, a.WorkItem.Lane);
        Assert.Equal(TelemetryProcessingLane.Ordinary, b.WorkItem.Lane);
        AssertAccounting(buffer, 2, 2, 0);
    }

    [Fact]
    public async Task LaterCriticalPacket_BypassesPendingOrdinaryPackets_ThenFifoResumes()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        buffer.SetOrdinaryDispatchPaused(true);
        var a = Admit(buffer, 10);
        var b = Admit(buffer, 20);
        var c = Admit(buffer, 30, DashboardIssueSeverity.Critical);

        var critical = await buffer.DequeueAsync();
        Assert.Same(c, critical.WorkItem);
        Assert.Equal(3L, critical.WorkItem.ArrivalSequence);
        Assert.Equal(1L, critical.DequeueSequence);
        Assert.Equal(TelemetryProcessingLane.Critical, critical.WorkItem.Lane);
        AssertAccounting(buffer, 3, 1, 2);

        buffer.SetOrdinaryDispatchPaused(false);
        Assert.Same(a, (await buffer.DequeueAsync()).WorkItem);
        Assert.Same(b, (await buffer.DequeueAsync()).WorkItem);
        AssertAccounting(buffer, 3, 3, 0);
    }

    [Fact]
    public async Task EveryDispatch_RechecksCriticalLane_AfterOrdinaryWorkWasDispatched()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var a = Admit(buffer, 10);
        var b = Admit(buffer, 20);
        Assert.Same(a, (await buffer.DequeueAsync()).WorkItem);

        var c = Admit(buffer, 30, DashboardIssueSeverity.Critical);
        Assert.Same(c, (await buffer.DequeueAsync()).WorkItem);
        Assert.Same(b, (await buffer.DequeueAsync()).WorkItem);
    }

    [Fact]
    public async Task EqualCriticalPriorities_UseArrivalSequenceAsStableTieBreaker()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var items = Enumerable.Range(0, 128)
            .Select(value => Admit(buffer, value, DashboardIssueSeverity.Critical))
            .ToArray();

        foreach (var expected in items)
        {
            var dispatched = await buffer.DequeueAsync();
            Assert.Same(expected, dispatched.WorkItem);
            Assert.Equal(expected.ArrivalSequence, dispatched.DequeueSequence);
        }

        AssertAccounting(buffer, 128, 128, 0);
    }

    [Fact]
    public void PriorityComparer_OrdersHigherSeverityFirst_AndEarlierArrivalWithinSeverity()
    {
        var queue = new PriorityQueue<string, TelemetryWorkPriority>(TelemetryWorkPriorityComparer.Instance);
        queue.Enqueue("warning", new TelemetryWorkPriority(DashboardIssueSeverity.Warning, 1));
        queue.Enqueue("later-critical", new TelemetryWorkPriority(DashboardIssueSeverity.Critical, long.MaxValue));
        queue.Enqueue("information", new TelemetryWorkPriority(DashboardIssueSeverity.Information, 2));
        queue.Enqueue("first-critical", new TelemetryWorkPriority(DashboardIssueSeverity.Critical, 3));

        Assert.Equal("first-critical", queue.Dequeue());
        Assert.Equal("later-critical", queue.Dequeue());
        Assert.Equal("warning", queue.Dequeue());
        Assert.Equal("information", queue.Dequeue());
    }

    [Fact]
    public async Task Admission_RetainsExactTypedPackets_AndNormalisesAcceptanceTime()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var source = DeviceIdentifier.FromMacAddress("aabbccddee03");
        var floating = new NumericTelemetryPacket<double>(source, 24.75, BaseTime);
        var integer = new NumericTelemetryPacket<int>(source, int.MinValue, BaseTime);
        var boolean = new TelemetryPacket<bool>(source, false, BaseTime);
        var accepted = BaseTime.ToOffset(TimeSpan.FromHours(2));

        var a = buffer.Enqueue(SensorId, floating, DashboardIssueSeverity.Information, accepted);
        var b = buffer.Enqueue(SensorId, integer, DashboardIssueSeverity.Warning, accepted);
        var c = buffer.Enqueue(SensorId, boolean, DashboardIssueSeverity.Information, accepted);

        Assert.Same(floating, a.Packet);
        Assert.Same(integer, b.Packet);
        Assert.Same(boolean, c.Packet);
        Assert.Equal(24.75, a.Packet.Payload);
        Assert.Equal(int.MinValue, b.Packet.Payload);
        Assert.False(c.Packet.Payload);
        Assert.Equal(TelemetryDataType.Float, a.DataType);
        Assert.Equal(TelemetryDataType.Integer, b.DataType);
        Assert.Equal(TelemetryDataType.Boolean, c.DataType);
        Assert.Equal(SensorId, a.SensorProfileId);
        Assert.Equal(source, a.SourceDeviceIdentifier);
        Assert.Equal(BaseTime, a.CapturedAtUtc);
        Assert.Equal(TimeSpan.Zero, a.AcceptedAtUtc.Offset);
        Assert.Equal(BaseTime, a.AcceptedAtUtc);
        Assert.Same(a, (await buffer.DequeueAsync()).WorkItem);
        Assert.Same(b, (await buffer.DequeueAsync()).WorkItem);
        Assert.Same(c, (await buffer.DequeueAsync()).WorkItem);
    }

    [Fact]
    public async Task Snapshot_IsDetachedAndReadOnly_AndShowsCriticalDispatchOrder()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var ordinary = Admit(buffer, 10);
        var firstCritical = Admit(buffer, 20, DashboardIssueSeverity.Critical);
        var secondCritical = Admit(buffer, 30, DashboardIssueSeverity.Critical);
        var before = buffer.GetSnapshot();

        Assert.Same(ordinary, Assert.Single(before.OrdinaryPending));
        Assert.Equal(new TelemetryWorkItem[] { firstCritical, secondCritical }, before.CriticalPending);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TelemetryWorkItem>)before.OrdinaryPending).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TelemetryWorkItem>)before.CriticalPending)[0] = ordinary);

        await buffer.DequeueAsync();
        Admit(buffer, 40);
        buffer.SetOrdinaryDispatchPaused(true);

        Assert.Equal(3L, before.AcceptedCount);
        Assert.Equal(0L, before.DispatchedCount);
        Assert.Equal(3, before.PendingCount);
        Assert.False(before.OrdinaryDispatchPaused);
        Assert.Equal(2, before.CriticalPendingCount);
        AssertAccounting(buffer, 4, 1, 3);
        Assert.True(buffer.GetSnapshot().OrdinaryDispatchPaused);
    }

    [Fact]
    public async Task EmptyBuffer_WaitsUntilAdmission_WithoutLosingWakeup()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var waiting = buffer.DequeueAsync(cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        var admitted = Admit(buffer, 10);
        Assert.Same(admitted, (await waiting).WorkItem);
        AssertAccounting(buffer, 1, 1, 0);
    }

    [Fact]
    public async Task Pause_BlocksOrdinaryDispatch_ButCriticalAdmissionWakesWaitingConsumer()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        buffer.SetOrdinaryDispatchPaused(true);
        Admit(buffer, 10);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var waiting = buffer.DequeueAsync(cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        var critical = Admit(buffer, 20, DashboardIssueSeverity.Critical);
        Assert.Same(critical, (await waiting).WorkItem);
        AssertAccounting(buffer, 2, 1, 1);
        Assert.True(buffer.GetSnapshot().OrdinaryDispatchPaused);
    }

    [Fact]
    public async Task Resume_WakesWaitingConsumer_AndRepeatedControlsDoNotConsumeWork()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        buffer.SetOrdinaryDispatchPaused(true);
        buffer.SetOrdinaryDispatchPaused(true);
        var admitted = Admit(buffer, 10);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var waiting = buffer.DequeueAsync(cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        buffer.SetOrdinaryDispatchPaused(false);
        buffer.SetOrdinaryDispatchPaused(false);
        Assert.Same(admitted, (await waiting).WorkItem);
        AssertAccounting(buffer, 1, 1, 0);
    }

    [Fact]
    public async Task AlreadyCancelledDispatch_DoesNotRemoveAvailableCriticalOrOrdinaryWork()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var ordinary = Admit(buffer, 10);
        var critical = Admit(buffer, 20, DashboardIssueSeverity.Critical);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            buffer.DequeueAsync(cancellation.Token).AsTask());

        AssertAccounting(buffer, 2, 0, 2);
        Assert.Same(critical, (await buffer.DequeueAsync()).WorkItem);
        Assert.Same(ordinary, (await buffer.DequeueAsync()).WorkItem);
    }

    [Fact]
    public async Task CancelledWait_PreservesPausedWork_AndDoesNotPoisonLaterWaits()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        buffer.SetOrdinaryDispatchPaused(true);
        var ordinary = Admit(buffer, 10);
        using var cancellation = new CancellationTokenSource();
        var waiting = buffer.DequeueAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        AssertAccounting(buffer, 1, 0, 1);
        using var subsequentCancellation = new CancellationTokenSource(TestTimeout);
        var subsequent = buffer.DequeueAsync(subsequentCancellation.Token).AsTask();
        var critical = Admit(buffer, 20, DashboardIssueSeverity.Critical);
        Assert.Same(critical, (await subsequent).WorkItem);

        buffer.SetOrdinaryDispatchPaused(false);
        Assert.Same(ordinary, (await buffer.DequeueAsync()).WorkItem);
    }

    [Fact]
    public async Task ConcurrentAdmission_AssignsUniqueContiguousSequences_AndKeepsEachLaneStable()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var admissions = Enumerable.Range(0, 512)
            .Select(value => Task.Run(() => Admit(buffer, value,
                value % 3 == 0 ? DashboardIssueSeverity.Critical : DashboardIssueSeverity.Information)))
            .ToArray();
        var admitted = await Task.WhenAll(admissions).WaitAsync(TestTimeout);

        Assert.Equal(Enumerable.Range(1, 512).Select(value => (long)value),
            admitted.Select(item => item.ArrivalSequence).OrderBy(value => value));
        AssertAccounting(buffer, 512, 0, 512);
        var expected = admitted.OrderByDescending(item => item.Lane)
            .ThenBy(item => item.ArrivalSequence).ToArray();

        for (var index = 0; index < expected.Length; index++)
        {
            var dispatched = await buffer.DequeueAsync();
            Assert.Same(expected[index], dispatched.WorkItem);
            Assert.Equal((long)index + 1, dispatched.DequeueSequence);
        }

        AssertAccounting(buffer, 512, 512, 0);
    }

    [Fact]
    public async Task ConcurrentProducersAndOneConsumer_DoNotLoseOrDuplicateAcceptedPackets()
    {
        const int count = 400;
        var buffer = new InMemoryTelemetryWorkBuffer();
        using var cancellation = new CancellationTokenSource(TestTimeout);

        async Task<TelemetryDispatch[]> ConsumeAsync()
        {
            var results = new TelemetryDispatch[count];
            for (var index = 0; index < count; index++)
            {
                results[index] = await buffer.DequeueAsync(cancellation.Token);
                var snapshot = buffer.GetSnapshot();
                Assert.Equal(snapshot.AcceptedCount, snapshot.DispatchedCount + snapshot.PendingCount);
            }

            return results;
        }

        var consuming = ConsumeAsync();
        var producers = Enumerable.Range(0, count)
            .Select(value => Task.Run(() => Admit(buffer, value,
                value % 2 == 0 ? DashboardIssueSeverity.Critical : DashboardIssueSeverity.Warning)))
            .ToArray();
        var accepted = await Task.WhenAll(producers).WaitAsync(TestTimeout);
        var dispatched = await consuming;

        Assert.Equal(accepted.Select(item => item.ArrivalSequence).OrderBy(value => value),
            dispatched.Select(item => item.WorkItem.ArrivalSequence).OrderBy(value => value));
        Assert.Equal(Enumerable.Range(1, count).Select(value => (long)value),
            dispatched.Select(item => item.DequeueSequence));

        foreach (var lane in Enum.GetValues<TelemetryProcessingLane>())
        {
            var sequences = dispatched.Where(item => item.WorkItem.Lane == lane)
                .Select(item => item.WorkItem.ArrivalSequence).ToArray();
            Assert.Equal(sequences.OrderBy(value => value), sequences);
        }

        AssertAccounting(buffer, count, count, 0);
    }

    [Theory]
    [InlineData("sensor")]
    [InlineData("packet")]
    [InlineData("timestamp")]
    [InlineData("severity")]
    [InlineData("type")]
    public void InvalidAdmission_DoesNotChangeCountsOrConsumeArrivalSequence(string invalidField)
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var packet = Packet(10);

        Assert.ThrowsAny<ArgumentException>(() =>
        {
            switch (invalidField)
            {
                case "sensor":
                    buffer.Enqueue(Guid.Empty, packet, DashboardIssueSeverity.Information, BaseTime);
                    break;
                case "packet":
                    buffer.Enqueue<int>(SensorId, null!, DashboardIssueSeverity.Information, BaseTime);
                    break;
                case "timestamp":
                    buffer.Enqueue(SensorId, packet, DashboardIssueSeverity.Information, default);
                    break;
                case "severity":
                    buffer.Enqueue(SensorId, packet, (DashboardIssueSeverity)999, BaseTime);
                    break;
                case "type":
                    buffer.Enqueue(SensorId,
                        new TelemetryPacket<decimal>(packet.SourceDeviceIdentifier, 1.25m, BaseTime),
                        DashboardIssueSeverity.Information, BaseTime);
                    break;
                default:
                    throw new InvalidOperationException("Unknown test input.");
            }
        });

        AssertAccounting(buffer, 0, 0, 0);
        Assert.Equal(1L, Admit(buffer, 20).ArrivalSequence);
    }

    private static TelemetryWorkItem<int> Admit(
        InMemoryTelemetryWorkBuffer buffer,
        int value,
        DashboardIssueSeverity severity = DashboardIssueSeverity.Information,
        DateTimeOffset? capturedAtUtc = null)
    {
        return buffer.Enqueue(SensorId, Packet(value, capturedAtUtc), severity, BaseTime);
    }

    private static TelemetryPacket<int> Packet(int value, DateTimeOffset? capturedAtUtc = null)
    {
        return new NumericTelemetryPacket<int>(
            DeviceIdentifier.FromUniqueIdentifier("processing-node-03"), value, capturedAtUtc ?? BaseTime);
    }

    private static void AssertAccounting(
        InMemoryTelemetryWorkBuffer buffer, long accepted, long dispatched, int pending)
    {
        var snapshot = buffer.GetSnapshot();
        Assert.Equal(accepted, snapshot.AcceptedCount);
        Assert.Equal(dispatched, snapshot.DispatchedCount);
        Assert.Equal(pending, snapshot.PendingCount);
        Assert.Equal(snapshot.AcceptedCount, snapshot.DispatchedCount + snapshot.PendingCount);
    }
}