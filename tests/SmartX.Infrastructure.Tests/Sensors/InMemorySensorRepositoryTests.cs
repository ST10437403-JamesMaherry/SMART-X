using SmartX.Domain.Sensors;
using SmartX.Infrastructure.Sensors;

namespace SmartX.Infrastructure.Tests.Sensors;

/// <summary>
/// Verifies the thread-safe Part 1 sensor repository independently from the
/// HTTP and Application layers.
/// </summary>
public sealed class InMemorySensorRepositoryTests
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
    public async Task TryAddAsync_WhenSensorValid_PersistsSensor()
    {
        var repository =
            new InMemorySensorRepository();

        var sensor =
            CreateSensor(
                "Sensor One",
                "AA:BB:CC:DD:EE:01");

        var added =
            await repository.TryAddAsync(sensor);

        var stored =
            await repository.GetByIdAsync(
                sensor.Id);

        Assert.True(added);
        Assert.Same(sensor, stored);
    }

    [Fact]
    public async Task TryAddAsync_WhenIdentifierDuplicate_ReturnsFalse()
    {
        var repository =
            new InMemorySensorRepository();

        var first =
            CreateSensor(
                "First Sensor",
                "AA:BB:CC:DD:EE:02");

        var duplicate =
            CreateSensor(
                "Duplicate Sensor",
                "aa-bb-cc-dd-ee-02");

        Assert.True(
            await repository.TryAddAsync(first));

        Assert.False(
            await repository.TryAddAsync(duplicate));

        var sensors =
            await repository.GetAllAsync();

        Assert.Single(sensors);
    }

    [Fact]
    public async Task GetAllAsync_WhenMultipleSensorsExist_ReturnsStableOrder()
    {
        var repository =
            new InMemorySensorRepository();

        var first =
            CreateSensor(
                "First Sensor",
                "AA:BB:CC:DD:EE:03",
                BaseTime);

        var second =
            CreateSensor(
                "Second Sensor",
                "AA:BB:CC:DD:EE:04",
                BaseTime.AddMinutes(1));

        await repository.TryAddAsync(second);
        await repository.TryAddAsync(first);

        var sensors =
            await repository.GetAllAsync();

        Assert.Equal(2, sensors.Count);

        Assert.Equal(
            first.Id,
            sensors[0].Id);

        Assert.Equal(
            second.Id,
            sensors[1].Id);
    }

    [Fact]
    public async Task GetByIdAsync_WhenSensorMissing_ReturnsNull()
    {
        var repository =
            new InMemorySensorRepository();

        var result =
            await repository.GetByIdAsync(
                Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_WhenSensorExists_PreservesUpdatedDetails()
    {
        var repository =
            new InMemorySensorRepository();

        var sensor =
            CreateSensor(
                "Original Sensor",
                "AA:BB:CC:DD:EE:05");

        await repository.TryAddAsync(sensor);

        sensor.UpdateDetails(
            "Updated Sensor",
            new DeploymentLocation(
                "Facility B",
                "Zone 2",
                "Node 5"),
            SensorCategory.PowerConsumption,
            TelemetryDataType.Integer,
            BaseTime.AddHours(1));

        var updated =
            await repository.UpdateAsync(sensor);

        var stored =
            await repository.GetByIdAsync(
                sensor.Id);

        Assert.True(updated);
        Assert.NotNull(stored);

        Assert.Equal(
            "Updated Sensor",
            stored.Name);

        Assert.Equal(
            "AA:BB:CC:DD:EE:05",
            stored.DeviceIdentifier.Value);
    }

    [Fact]
    public async Task UpdateAsync_WhenSensorMissing_ReturnsFalse()
    {
        var repository =
            new InMemorySensorRepository();

        var sensor =
            CreateSensor(
                "Missing Sensor",
                "AA:BB:CC:DD:EE:06");

        var result =
            await repository.UpdateAsync(sensor);

        Assert.False(result);
    }

    [Fact]
    public async Task TryAddAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var repository =
            new InMemorySensorRepository();

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => repository.TryAddAsync(
                CreateSensor(
                    "Cancelled Sensor",
                    "AA:BB:CC:DD:EE:07"),
                cancellationSource.Token));
    }

    [Fact]
    public async Task TryAddAsync_WhenRegistrationsConcurrent_AllowsOnlyOneDuplicateIdentifier()
    {
        var repository =
            new InMemorySensorRepository();

        var sensors =
            Enumerable
                .Range(0, 20)
                .Select(
                    index =>
                        CreateSensor(
                            $"Concurrent Sensor {index}",
                            "AA:BB:CC:DD:EE:08"))
                .ToArray();

        var attempts =
            sensors.Select(
                sensor =>
                    Task.Run(
                        async () =>
                            await repository.TryAddAsync(
                                sensor)));

        var results =
            await Task.WhenAll(attempts);

        Assert.Equal(
            1,
            results.Count(result => result));

        var persisted =
            await repository.GetAllAsync();

        Assert.Single(persisted);

        Assert.Equal(
            "AA:BB:CC:DD:EE:08",
            persisted[0].DeviceIdentifier.Value);
    }

    private static SensorProfile CreateSensor(
        string name,
        string macAddress,
        DateTimeOffset? createdAtUtc = null)
    {
        return SensorProfile.Create(
            name,
            DeviceIdentifier.FromMacAddress(
                macAddress),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            createdAtUtc ?? BaseTime);
    }
}