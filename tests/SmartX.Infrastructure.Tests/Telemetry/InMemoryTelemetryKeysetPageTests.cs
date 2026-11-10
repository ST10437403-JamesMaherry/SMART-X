using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Telemetry;

namespace SmartX.Infrastructure.Tests.Telemetry;

/// <summary>
/// Validates that API paging reads the existing sensor/type AVL index and
/// leaves the original ingestion-order telemetry endpoint untouched.
/// </summary>
public sealed class InMemoryTelemetryKeysetPageTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier Device =
        DeviceIdentifier.FromUniqueIdentifier("repo-page-test");

    [Fact]
    public async Task GetRangePageAsync_PreservesOutOfOrderAndEqualTimeReadings()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensor = Guid.NewGuid();
        await repository.AddAsync(sensor, new TelemetryPacket<int>(
            Device, 40, BaseTime.AddSeconds(2)));
        await repository.AddAsync(sensor, new TelemetryPacket<int>(
            Device, 20, BaseTime.AddSeconds(1)));
        await repository.AddAsync(sensor, new TelemetryPacket<int>(
            Device, 21, BaseTime.AddSeconds(1)));
        await repository.AddAsync(sensor, new TelemetryPacket<int>(
            Device, 10, BaseTime));

        AvlRangePage<int> first = await repository.GetRangePageAsync<int>(
            sensor, BaseTime, BaseTime.AddSeconds(3), 2);
        Assert.True(first.HasMore);
        Assert.Equal(new[] { 10, 20 }, first.Entries.Select(e => e.Packet.Payload));

        AvlRangeEntry<int> cursor = first.Entries[^1];
        AvlRangePage<int> second = await repository.GetRangePageAsync<int>(
            sensor, BaseTime, BaseTime.AddSeconds(3), 2,
            cursor.Packet.CapturedAtUtc, cursor.TimestampSequence);

        Assert.Equal(new[] { 21, 40 }, second.Entries.Select(e => e.Packet.Payload));
        Assert.False(second.HasMore);

        IReadOnlyList<TelemetryPacket<int>> legacy =
            await repository.GetRecentAsync<int>(sensor, 10);
        Assert.Equal(new[] { 40, 20, 21, 10 },
            legacy.Select(p => p.Payload));
    }

    [Fact]
    public async Task GetRangePageAsync_MissingSeriesHasEmptyPage()
    {
        var repository = new InMemoryTelemetryRepository();
        AvlRangePage<bool> result = await repository.GetRangePageAsync<bool>(
            Guid.NewGuid(), BaseTime, BaseTime.AddHours(1), 10);

        Assert.Empty(result.Entries);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task GetRangePageAsync_RejectedCursorDoesNotChangeAcceptedData()
    {
        var repository = new InMemoryTelemetryRepository();
        Guid sensor = Guid.NewGuid();
        await repository.AddAsync(sensor, new TelemetryPacket<double>(
            Device, 3.25, BaseTime));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.GetRangePageAsync<double>(sensor, BaseTime,
                BaseTime.AddSeconds(1), 10, BaseTime, 4));

        IReadOnlyList<TelemetryPacket<double>> recent =
            await repository.GetRecentAsync<double>(sensor, 5);
        Assert.Equal(3.25, Assert.Single(recent).Payload);
    }
}