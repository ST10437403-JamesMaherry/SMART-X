using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Telemetry;

namespace SmartX.Infrastructure.Tests.Telemetry;

/// <summary>
/// Verifies the thread-safe strongly typed telemetry persistence used by the
/// Smart-X Part 1 backend.
/// </summary>
public sealed class InMemoryTelemetryRepositoryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            10,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_WithDoublePacket_PreservesStrongTypeAndValue()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        var packet =
            new NumericTelemetryPacket<double>(
                CreateIdentifier(),
                24.75,
                BaseTime);

        await repository.AddAsync(
            sensorId,
            packet);

        var stored =
            await repository.GetRecentAsync<double>(
                sensorId,
                10);

        var result =
            Assert.Single(stored);

        Assert.IsType<
            NumericTelemetryPacket<double>>(
            result);

        Assert.Equal(
            24.75,
            result.Payload);
    }

    [Fact]
    public async Task AddAsync_WithBooleanFalse_PreservesLegitimateFalseValue()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        await repository.AddAsync(
            sensorId,
            new TelemetryPacket<bool>(
                CreateIdentifier(),
                false,
                BaseTime));

        var stored =
            await repository.GetRecentAsync<bool>(
                sensorId,
                10);

        var result =
            Assert.Single(stored);

        Assert.False(result.Payload);
    }

    [Fact]
    public async Task AddBatchAsync_WhenUnevenHistory_PreservesPacketOrder()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        TelemetryPacket<int>[][] batches =
        [
            [
                CreateIntegerPacket(10, 0),
                CreateIntegerPacket(11, 1)
            ],
            [
                CreateIntegerPacket(20, 2)
            ],
            [
                CreateIntegerPacket(30, 3),
                CreateIntegerPacket(31, 4)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(batches);

        await repository.AddBatchAsync(
            sensorId,
            history);

        var stored =
            await repository.GetRecentAsync<int>(
                sensorId,
                10);

        Assert.Equal(
            [10, 11, 20, 30, 31],
            stored
                .Select(packet => packet.Payload)
                .ToArray());
    }

    [Fact]
    public async Task GetRecentAsync_WhenLimitSmallerThanHistory_ReturnsNewestInIngestionOrder()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        await repository.AddAsync(
            sensorId,
            CreateIntegerPacket(10, 0));

        await repository.AddAsync(
            sensorId,
            CreateIntegerPacket(20, 1));

        await repository.AddAsync(
            sensorId,
            CreateIntegerPacket(30, 2));

        var recent =
            await repository.GetRecentAsync<int>(
                sensorId,
                2);

        Assert.Equal(
            [20, 30],
            recent
                .Select(packet => packet.Payload)
                .ToArray());
    }

    [Fact]
    public async Task GetRecentAsync_WhenSensorHasNoTelemetry_ReturnsEmptyCollection()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var result =
            await repository.GetRecentAsync<double>(
                Guid.NewGuid(),
                10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Repository_KeepsDifferentSensorsIsolated()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var firstSensor =
            Guid.NewGuid();

        var secondSensor =
            Guid.NewGuid();

        await repository.AddAsync(
            firstSensor,
            CreateIntegerPacket(100, 0));

        await repository.AddAsync(
            secondSensor,
            CreateIntegerPacket(200, 1));

        var first =
            await repository.GetRecentAsync<int>(
                firstSensor,
                10);

        var second =
            await repository.GetRecentAsync<int>(
                secondSensor,
                10);

        Assert.Equal(
            100,
            Assert.Single(first).Payload);

        Assert.Equal(
            200,
            Assert.Single(second).Payload);
    }

    [Fact]
    public async Task Repository_AllowsSeparateStrongTypesForSameSensor()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        await repository.AddAsync(
            sensorId,
            new NumericTelemetryPacket<double>(
                CreateIdentifier(),
                20.5,
                BaseTime));

        await repository.AddAsync(
            sensorId,
            new NumericTelemetryPacket<int>(
                CreateIdentifier(),
                21,
                BaseTime.AddSeconds(1)));

        var floatingPoint =
            await repository.GetRecentAsync<double>(
                sensorId,
                10);

        var integer =
            await repository.GetRecentAsync<int>(
                sensorId,
                10);

        Assert.Equal(
            20.5,
            Assert.Single(floatingPoint).Payload);

        Assert.Equal(
            21,
            Assert.Single(integer).Payload);
    }

    [Fact]
    public async Task AddAsync_WhenCancelled_DoesNotPersistPacket()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => repository.AddAsync(
                sensorId,
                CreateIntegerPacket(
                    10,
                    0),
                cancellationSource.Token));

        var stored =
            await repository.GetRecentAsync<int>(
                sensorId,
                10);

        Assert.Empty(stored);
    }

    [Fact]
    public async Task ConcurrentAdds_DoNotLoseTelemetryPackets()
    {
        var repository =
            new InMemoryTelemetryRepository();

        var sensorId =
            Guid.NewGuid();

        var tasks =
            Enumerable
                .Range(0, 100)
                .Select(
                    value =>
                        Task.Run(
                            () => repository.AddAsync(
                                sensorId,
                                CreateIntegerPacket(
                                    value,
                                    value))))
                .ToArray();

        await Task.WhenAll(tasks);

        var stored =
            await repository.GetRecentAsync<int>(
                sensorId,
                100);

        Assert.Equal(
            100,
            stored.Count);

        Assert.Equal(
            Enumerable.Range(0, 100),
            stored
                .Select(packet => packet.Payload)
                .OrderBy(value => value));
    }

    private static NumericTelemetryPacket<int> CreateIntegerPacket(
        int payload,
        int secondsAfterBase)
    {
        return new NumericTelemetryPacket<int>(
            CreateIdentifier(),
            payload,
            BaseTime.AddSeconds(
                secondsAfterBase));
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:60");
    }
}