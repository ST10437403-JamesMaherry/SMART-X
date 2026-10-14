using SmartX.Application.Realtime;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Infrastructure.Tests.Realtime;

public sealed class TelemetryProcessingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentifier Device = DeviceIdentifier.FromUniqueIdentifier("processing-node");
    private readonly Guid _sensorId = Guid.NewGuid();
    private readonly InMemoryTelemetryWorkBuffer _buffer = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderOrdinaryDispatch_CannotReplaceNewerCriticalObservation(bool equalCaptureTime)
    {
        var state = new InMemoryTelemetryProcessingStore(_buffer);
        var older = Enqueue(10, Now, DashboardIssueSeverity.Information);
        var newer = Enqueue(100, equalCaptureTime ? Now : Now.AddSeconds(1), DashboardIssueSeverity.Critical);
        var critical = await CompleteNextAsync(state, Now.AddSeconds(2));
        var earlierSnapshot = state.GetSnapshot();
        var ordinary = await CompleteNextAsync(state, Now.AddSeconds(3));

        Assert.Same(newer, critical.WorkItem);
        Assert.Same(older, ordinary.WorkItem);
        var device = state.GetDeviceState(_sensorId);
        Assert.NotNull(device);
        Assert.Same(newer, device.LatestObservation);
        Assert.Equal(Now.AddSeconds(3), device.LastProcessedAtUtc);
        var current = state.GetSnapshot();
        Assert.Equal(2, current.ProcessedCount);
        Assert.Equal(0, current.OutstandingDispatchedCount);
        Assert.Equal(new long[] { 2, 1 }, current.RecentDispatches.Select(x => x.Dispatch.WorkItem.ArrivalSequence));
        Assert.False(current.RecentDispatches[1].UpdatedLatestObservation);
        Assert.Equal(1, earlierSnapshot.ProcessedCount);
        Assert.Single(earlierSnapshot.RecentDispatches);
    }

    [Fact]
    public async Task LateHistoricalAdmission_DoesNotReplaceNewerCapturedObservation()
    {
        var state = new InMemoryTelemetryProcessingStore(_buffer);
        var newest = Enqueue(30, Now, DashboardIssueSeverity.Information);
        await CompleteNextAsync(state, Now);
        Enqueue(1, Now.AddDays(-1), DashboardIssueSeverity.Information);
        await CompleteNextAsync(state, Now.AddSeconds(1));
        Assert.Same(newest, state.GetDeviceState(_sensorId)!.LatestObservation);
        Assert.Equal(2, state.GetSnapshot().ProcessedCount);
    }

    [Fact]
    public async Task DispatchEvidence_IsBoundedAndDetached_WhileTotalsAccountForEveryPacket()
    {
        var state = new InMemoryTelemetryProcessingStore(_buffer);
        for (var i = 0; i < 151; i++) Enqueue(i, Now.AddSeconds(i), DashboardIssueSeverity.Information);
        var pending = state.GetSnapshot();
        for (var i = 0; i < 151; i++) await CompleteNextAsync(state, Now.AddMinutes(5));
        var snapshot = state.GetSnapshot();
        Assert.Equal(151, snapshot.ProcessedCount);
        Assert.Equal(151, snapshot.Buffer.AcceptedCount);
        Assert.Equal(100, snapshot.RecentDispatches.Count);
        Assert.Equal(52, snapshot.RecentDispatches[0].Dispatch.DequeueSequence);
        Assert.Equal(151, pending.Buffer.PendingCount);
        Assert.Empty(pending.RecentDispatches);
        var mutableView = Assert.IsAssignableFrom<IList<TelemetryProcessingRecord>>(snapshot.RecentDispatches);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
        var response = TelemetryProcessingMapper.ToResponse(snapshot);
        Assert.Equal(150, response.RecentDispatches[^1].Telemetry.IntegerValue);
    }

    [Fact]
    public async Task Fault_PreservesExactDispatchPendingWorkAndVisibleFailure_AfterShutdown()
    {
        var state = new InMemoryTelemetryProcessingStore(_buffer);
        var first = Enqueue(10, Now, DashboardIssueSeverity.Information);
        Enqueue(11, Now, DashboardIssueSeverity.Information);
        var dispatch = await _buffer.DequeueAsync();
        state.Begin(dispatch, Now);
        state.MarkFaulted(Now);
        state.MarkStopped(Now);
        var snapshot = state.GetSnapshot();
        Assert.Equal(TelemetryWorkerStatus.Faulted, snapshot.WorkerStatus);
        Assert.Same(first, snapshot.CurrentDispatch!.WorkItem);
        Assert.NotNull(snapshot.Failure);
        Assert.Equal(0, snapshot.ProcessedCount);
        Assert.Equal(1, snapshot.Buffer.PendingCount);
        Assert.Equal(1, snapshot.OutstandingDispatchedCount);
        Assert.Equal(TelemetryDispatchOutcome.Failed, Assert.Single(snapshot.RecentDispatches).Outcome);
        Assert.Equal(snapshot.Buffer.AcceptedCount,
            snapshot.Buffer.PendingCount + snapshot.ProcessedCount + snapshot.OutstandingDispatchedCount);
    }

    private TelemetryWorkItem<int> Enqueue(int value, DateTimeOffset captured, DashboardIssueSeverity severity)
        => _buffer.Enqueue(_sensorId, new TelemetryPacket<int>(Device, value, captured), severity, Now);

    private async Task<TelemetryDispatch> CompleteNextAsync(InMemoryTelemetryProcessingStore state, DateTimeOffset at)
    {
        var dispatch = await _buffer.DequeueAsync();
        state.Begin(dispatch, at);
        state.Complete(dispatch, at);
        return dispatch;
    }
}