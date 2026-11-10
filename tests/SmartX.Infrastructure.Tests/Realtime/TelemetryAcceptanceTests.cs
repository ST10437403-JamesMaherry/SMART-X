using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Telemetry;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;
using SmartX.Infrastructure.Telemetry;

namespace SmartX.Infrastructure.Tests.Realtime;

/// <summary>Checks the acceptance boundary against the real typed repository and buffers.</summary>
public sealed class TelemetryAcceptanceTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterPersistence_DoesNotRevokeSingleOrBatchAdmission(bool batch)
    {
        using var requestCancellation = new CancellationTokenSource();
        var repository = new ControlledRepository { AfterWrite = requestCancellation.Cancel };
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        var service = CreateService(repository, buffer, observer, gate);
        var sensor = Sensor();
        var first = Packet(sensor, 10, 0);
        var second = Packet(sensor, 30, 1);

        if (batch)
        {
            await service.AcceptBatchAsync(sensor,
                HistoricalTelemetryBuffer<int>.FromJaggedBatches([[first, second]]), requestCancellation.Token);
        }
        else
        {
            await service.AcceptAsync(sensor, first, requestCancellation.Token);
        }

        Assert.True(requestCancellation.IsCancellationRequested);
        var stored = await repository.Inner.GetRecentAsync<int>(sensor.Id, 10);
        var queued = PendingByArrival(buffer);
        Assert.Equal(batch ? 2 : 1, queued.Length);
        Assert.Same(first, Assert.IsType<TelemetryWorkItem<int>>(queued[0]).Packet);
        for (var index = 0; index < stored.Count; index++)
        {
            Assert.Same(stored[index], Assert.IsType<TelemetryWorkItem<int>>(queued[index]).Packet);
        }
        Assert.Single(observer.Observations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailure_CreatesNoWorkOrOperationalObservation(bool batch)
    {
        var repository = new ControlledRepository { FailWrite = true };
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        var service = CreateService(repository, buffer, observer, gate);
        var sensor = Sensor();
        var packet = Packet(sensor, 10, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => batch
            ? service.AcceptBatchAsync(sensor, HistoricalTelemetryBuffer<int>.FromJaggedBatches([[packet]]))
            : service.AcceptAsync(sensor, packet));

        Assert.Equal(0L, buffer.GetSnapshot().AcceptedCount);
        Assert.Empty(observer.Observations);
        Assert.Empty(await repository.Inner.GetRecentAsync<int>(sensor.Id, 10));
    }

    [Fact]
    public async Task CancelledBeforePersistence_LeavesBothStoresEmpty()
    {
        var repository = new ControlledRepository();
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sensor = Sensor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(repository, buffer, observer, gate).AcceptAsync(
                sensor, Packet(sensor, 10, 0), cancellation.Token));
        Assert.Equal(0L, buffer.GetSnapshot().AcceptedCount);
        Assert.Empty(await repository.Inner.GetRecentAsync<int>(sensor.Id, 10));
        Assert.Empty(observer.Observations);
    }

    [Fact]
    public async Task BatchOver100_RetainsEveryExactPacket_AndObservesOnlyTheFinalPair()
    {
        var repository = new ControlledRepository();
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        var sensor = Sensor();
        var packets = Enumerable.Range(0, 151)
            .Select(index => Packet(sensor, index == 75 ? 100 : 10, index)).ToArray();
        await CreateService(repository, buffer, observer, gate).AcceptBatchAsync(sensor,
            HistoricalTelemetryBuffer<int>.FromJaggedBatches([packets[..75], [], packets[75..]]));

        var queued = PendingByArrival(buffer);
        Assert.Equal(151, queued.Length);
        for (var index = 0; index < packets.Length; index++)
        {
            Assert.Same(packets[index], Assert.IsType<TelemetryWorkItem<int>>(queued[index]).Packet);
        }
        Assert.False(Assert.Single(observer.Observations).IsAnomaly);
        Assert.Equal(new[] { 1 }, repository.RecentLimits);
        Assert.Equal(2, buffer.GetSnapshot().CriticalPendingCount);
    }

    [Fact]
    public async Task WaitingRequestCancellation_DoesNotBlockTheNextRequest()
    {
        var repository = new ControlledRepository();
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        var service = CreateService(repository, buffer, observer, gate);
        var sensor = Sensor();
        var lease = await gate.EnterAsync(sensor.Id, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = service.AcceptAsync(sensor, Packet(sensor, 10, 0), cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        lease.Dispose();

        await service.AcceptAsync(sensor, Packet(sensor, 20, 1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1L, buffer.GetSnapshot().AcceptedCount);
    }

    [Fact]
    public async Task EmptyBatch_PersistsItsBoundaryButCreatesNoWorkOrObservation()
    {
        var repository = new ControlledRepository();
        var buffer = new InMemoryTelemetryWorkBuffer();
        var observer = new RecordingObserver();
        using var gate = new TelemetryIngestionGate();
        await CreateService(repository, buffer, observer, gate).AcceptBatchAsync(Sensor(),
            HistoricalTelemetryBuffer<int>.FromJaggedBatches([[], []]));
        Assert.Equal(0L, buffer.GetSnapshot().AcceptedCount);
        Assert.Empty(observer.Observations);
    }

    private static TelemetryAcceptanceService CreateService(ControlledRepository repository,
        ITelemetryWorkBuffer buffer, ITelemetryAcceptanceObserver observer, TelemetryIngestionGate gate)
        => new(repository, buffer, gate, new TelemetryAnomalyClassifier(new AdaptiveAnomalyPolicy()),
            observer, TimeProvider.System);

    private static TelemetryWorkItem[] PendingByArrival(InMemoryTelemetryWorkBuffer buffer)
    {
        var snapshot = buffer.GetSnapshot();
        return snapshot.OrdinaryPending.Concat(snapshot.CriticalPending)
            .OrderBy(item => item.ArrivalSequence).ToArray();
    }

    private static SensorProfile Sensor() => SensorProfile.Create("Acceptance sensor",
        DeviceIdentifier.FromUniqueIdentifier("acceptance-node"),
        new DeploymentLocation("Facility", "Zone", "Node"), SensorCategory.Environmental,
        TelemetryDataType.Integer, BaseTime.AddHours(-1));

    private static TelemetryPacket<int> Packet(SensorProfile sensor, int value, int seconds)
        => new NumericTelemetryPacket<int>(sensor.DeviceIdentifier, value, BaseTime.AddSeconds(seconds));

    private sealed class RecordingObserver : ITelemetryAcceptanceObserver
    {
        public List<TelemetryAnomalyObservation> Observations { get; } = [];
        public Task ObserveAsync(Guid sensorProfileId, TelemetryAnomalyObservation observation)
        {
            Observations.Add(observation);
            return Task.CompletedTask;
        }
    }

    /// <summary>Injects failure/cancellation at the real repository's acceptance boundary.</summary>
    private sealed class ControlledRepository : ITelemetryRepository
    {
        public InMemoryTelemetryRepository Inner { get; } = new();
        public Action? AfterWrite { get; init; }
        public bool FailWrite { get; init; }
        public List<int> RecentLimits { get; } = [];

        public async Task AddAsync<T>(Guid sensorProfileId, TelemetryPacket<T> packet,
            CancellationToken cancellationToken = default) where T : struct
        {
            if (FailWrite) throw new InvalidOperationException("Injected persistence failure.");
            await Inner.AddAsync(sensorProfileId, packet, cancellationToken);
            AfterWrite?.Invoke();
        }

        public async Task AddBatchAsync<T>(Guid sensorProfileId, HistoricalTelemetryBuffer<T> history,
            CancellationToken cancellationToken = default) where T : struct
        {
            if (FailWrite) throw new InvalidOperationException("Injected persistence failure.");
            await Inner.AddBatchAsync(sensorProfileId, history, cancellationToken);
            AfterWrite?.Invoke();
        }

        public Task<IReadOnlyList<TelemetryPacket<T>>> GetRecentAsync<T>(Guid sensorProfileId, int limit,
            CancellationToken cancellationToken = default) where T : struct
        {
            RecentLimits.Add(limit);
            return Inner.GetRecentAsync<T>(sensorProfileId, limit, cancellationToken);
        }

        /// <summary>Forward range reads to the real AVL-backed test store.</summary>
        public Task<IReadOnlyList<TelemetryPacket<T>>> GetRangeAsync<T>(
            Guid sensorProfileId,
            DateTimeOffset fromInclusiveUtc,
            DateTimeOffset toInclusiveUtc,
            int limit,
            CancellationToken cancellationToken = default) where T : struct
        {
            return Inner.GetRangeAsync<T>(
                sensorProfileId, fromInclusiveUtc, toInclusiveUtc, limit,
                cancellationToken);
        }
    }
}