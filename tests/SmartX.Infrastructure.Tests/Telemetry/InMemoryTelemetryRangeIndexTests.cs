using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Telemetry;

namespace SmartX.Infrastructure.Tests.Telemetry;

/// <summary>
/// P3-03 contract tests for the actual accepted-packet AVL secondary index.
/// These assert object identity, type isolation and the unchanged recent API;
/// larger adversarial and API cases are added in Commit 3.
/// </summary>
public sealed class InMemoryTelemetryRangeIndexTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier Device =
        DeviceIdentifier.FromMacAddress("AA:BB:CC:DD:EE:61");

    [Fact]
    public async Task SingleWrites_AreChronologicalInAvl_AndAppendOrderedInRecentHistory()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensorId = Guid.NewGuid();
        TelemetryPacket<int> later = Packet(30, 30);
        TelemetryPacket<int> equalFirst = Packet(20, 20);
        TelemetryPacket<int> early = Packet(10, 10);
        TelemetryPacket<int> equalSecond = Packet(21, 20);

        foreach (TelemetryPacket<int> packet in new[]
                 { later, equalFirst, early, equalSecond })
        {
            await repository.AddAsync(sensorId, packet);
        }

        IReadOnlyList<TelemetryPacket<int>> range =
            await repository.GetRangeAsync<int>(
                sensorId, BaseTime.AddSeconds(10), BaseTime.AddSeconds(30), 10);
        IReadOnlyList<TelemetryPacket<int>> recent =
            await repository.GetRecentAsync<int>(sensorId, 10);

        Assert.Equal([10, 20, 21, 30], range.Select(packet => packet.Payload).ToArray());
        Assert.Same(early, range[0]);
        Assert.Same(equalFirst, range[1]);
        Assert.Same(equalSecond, range[2]);
        Assert.Same(later, range[3]);
        Assert.Equal([30, 20, 10, 21], recent.Select(packet => packet.Payload).ToArray());
    }

    [Fact]
    public async Task JaggedBatch_IndexesExactOriginalPacketInstances()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensorId = Guid.NewGuid();
        TelemetryPacket<int> latest = Packet(90, 90);
        TelemetryPacket<int> earliest = Packet(10, 10);
        TelemetryPacket<int> middle = Packet(40, 40);

        TelemetryPacket<int>[][] batches =
        [
            [latest, earliest],
            [],
            [middle]
        ];

        await repository.AddBatchAsync(
            sensorId,
            HistoricalTelemetryBuffer<int>.FromJaggedBatches(batches));

        IReadOnlyList<TelemetryPacket<int>> indexed =
            await repository.GetRangeAsync<int>(
                sensorId, BaseTime, BaseTime.AddMinutes(2), 10);

        Assert.Equal(3, indexed.Count);
        Assert.Same(earliest, indexed[0]);
        Assert.Same(middle, indexed[1]);
        Assert.Same(latest, indexed[2]);
        // Resolve the async repository call before asserting its payload order.
        // A collection-expression expectation must not span an await boundary.
        IReadOnlyList<TelemetryPacket<int>> recent =
            await repository.GetRecentAsync<int>(sensorId, 10);

        Assert.Equal(
            new[] { 90, 10, 40 },
            recent.Select(packet => packet.Payload).ToArray());
    }

    [Fact]
    public async Task Indexes_AreIsolatedBySensorAndNativePayloadType()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensorId = Guid.NewGuid();
        Guid otherSensorId = Guid.NewGuid();
        var floating = new NumericTelemetryPacket<double>(Device, 12.75, BaseTime);
        var boolean = new TelemetryPacket<bool>(Device, false, BaseTime);
        var integer = Packet(42, 0);

        await repository.AddAsync(sensorId, floating);
        await repository.AddAsync(sensorId, boolean);
        await repository.AddAsync(otherSensorId, integer);

        Assert.Same(floating, Assert.Single(
            await repository.GetRangeAsync<double>(sensorId, BaseTime, BaseTime, 10)));
        Assert.Same(boolean, Assert.Single(
            await repository.GetRangeAsync<bool>(sensorId, BaseTime, BaseTime, 10)));
        Assert.Empty(await repository.GetRangeAsync<int>(
            sensorId, BaseTime, BaseTime, 10));
        Assert.Same(integer, Assert.Single(
            await repository.GetRangeAsync<int>(otherSensorId, BaseTime, BaseTime, 10)));
    }

    [Fact]
    public async Task InvalidRangeOrLimit_IsRejectedEvenWithoutStoredPackets()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensorId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.GetRangeAsync<int>(Guid.Empty, BaseTime, BaseTime, 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.GetRangeAsync<int>(
                sensorId, BaseTime.AddSeconds(1), BaseTime, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetRangeAsync<int>(sensorId, BaseTime, BaseTime, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetRangeAsync<int>(
                sensorId, BaseTime, BaseTime, AvlTelemetryIndex<int>.MaxRangeLimit + 1));
    }

    [Fact]
    public async Task CancelledBatch_DoesNotAppearInEitherHistory()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensorId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HistoricalTelemetryBuffer<int> batch =
            HistoricalTelemetryBuffer<int>.FromJaggedBatches(
                [[Packet(1, 1), Packet(2, 2)]]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.AddBatchAsync(sensorId, batch, cancellation.Token));

        Assert.Empty(await repository.GetRecentAsync<int>(sensorId, 10));
        Assert.Empty(await repository.GetRangeAsync<int>(
            sensorId, BaseTime, BaseTime.AddSeconds(10), 10));
    }

    private static TelemetryPacket<int> Packet(int value, int seconds)
    {
        return new TelemetryPacket<int>(
            Device, value, BaseTime.AddSeconds(seconds));
    }
}