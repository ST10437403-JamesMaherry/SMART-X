using SmartX.Domain.Sensors;
using SmartX.Infrastructure.Sensors;

namespace SmartX.Infrastructure.Tests.Sensors;

/// <summary>
/// Verifies Part 2 canonical lookup, paired-index consistency and concurrency
/// while the existing repository tests continue covering Part 1 behaviour.
/// </summary>
public sealed class CanonicalSensorRepositoryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:31")]
    [InlineData("aa-bb-cc-dd-ee-31")]
    [InlineData("aabbccddee31")]
    [InlineData("  aa:bb:cc:dd:ee:31  ")]
    public async Task GetByDeviceIdentifierAsync_WhenMacEquivalent_ReturnsRegisteredProfile(
        string lookupValue)
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor(DeviceIdentifier.FromMacAddress("AA:BB:CC:DD:EE:31"));

        Assert.True(await repository.TryAddAsync(sensor));

        var result = await repository.GetByDeviceIdentifierAsync(
            DeviceIdentifier.FromMacAddress(lookupValue));

        Assert.Same(sensor, result);
        Assert.Same(sensor, await repository.GetByIdAsync(sensor.Id));
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenCustomIdentifiersDifferByCase_KeepsBoth()
    {
        var repository = new InMemorySensorRepository();
        var upper = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("Node-Case"));
        var lower = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("node-case"));

        Assert.True(await repository.TryAddAsync(upper));
        Assert.True(await repository.TryAddAsync(lower));

        Assert.Same(
            upper,
            await repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromUniqueIdentifier("  Node-Case  ")));

        Assert.Same(
            lower,
            await repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromUniqueIdentifier("node-case")));

        Assert.Null(
            await repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromUniqueIdentifier("NODE-CASE")));

        Assert.Equal(2, (await repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenInputSharedAcrossKinds_KeepsIdentitiesSeparate()
    {
        const string identifier = "AABBCCDDEE32";

        var repository = new InMemorySensorRepository();
        var mac = CreateSensor(DeviceIdentifier.FromMacAddress(identifier));
        var custom = CreateSensor(DeviceIdentifier.FromUniqueIdentifier(identifier));

        Assert.True(await repository.TryAddAsync(mac));
        Assert.True(await repository.TryAddAsync(custom));

        Assert.Same(
            mac,
            await repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromMacAddress(identifier)));

        Assert.Same(
            custom,
            await repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromUniqueIdentifier(identifier)));

        Assert.Same(mac, await repository.GetByIdAsync(mac.Id));
        Assert.Same(custom, await repository.GetByIdAsync(custom.Id));
        Assert.Equal(2, (await repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenIdentifierMissing_ReturnsNull()
    {
        var repository = new InMemorySensorRepository();

        var result = await repository.GetByDeviceIdentifierAsync(
            DeviceIdentifier.FromUniqueIdentifier("unregistered-device"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenIdentifierNull_ThrowsArgumentNullException()
    {
        var repository = new InMemorySensorRepository();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.GetByDeviceIdentifierAsync(null!));
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var repository = new InMemorySensorRepository();

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetByDeviceIdentifierAsync(
                DeviceIdentifier.FromUniqueIdentifier("cancelled-device"),
                cancellationSource.Token));
    }

    [Fact]
    public async Task UpdateAsync_WhenProfileChanges_BothLookupsExposeCurrentMetadataAndStatus()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("updated-device"));

        Assert.True(await repository.TryAddAsync(sensor));

        sensor.UpdateDetails(
            "Updated Device",
            new DeploymentLocation("Facility B", "Zone 2", "Node 2"),
            SensorCategory.PowerConsumption,
            TelemetryDataType.Integer,
            BaseTime.AddMinutes(1));

        sensor.ChangeStatus(SensorStatus.Online, BaseTime.AddMinutes(2));

        Assert.True(await repository.UpdateAsync(sensor));

        var byIdentifier = await repository.GetByDeviceIdentifierAsync(
            DeviceIdentifier.FromUniqueIdentifier("updated-device"));

        var byId = await repository.GetByIdAsync(sensor.Id);

        Assert.NotNull(byIdentifier);
        Assert.Same(sensor, byIdentifier);
        Assert.Same(byId, byIdentifier);
        Assert.Equal("Updated Device", byIdentifier.Name);
        Assert.Equal("Facility B", byIdentifier.DeploymentLocation.Facility);
        Assert.Equal(SensorCategory.PowerConsumption, byIdentifier.Category);
        Assert.Equal(TelemetryDataType.Integer, byIdentifier.DataType);
        Assert.Equal(SensorStatus.Online, byIdentifier.Status);
        Assert.Equal(BaseTime.AddMinutes(2), byIdentifier.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_WhenProfileMissing_DoesNotCreateEitherIndexEntry()
    {
        var repository = new InMemorySensorRepository();
        var missing = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("missing-update"));

        Assert.False(await repository.UpdateAsync(missing));
        Assert.Null(await repository.GetByIdAsync(missing.Id));
        Assert.Null(await repository.GetByDeviceIdentifierAsync(missing.DeviceIdentifier));
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_WhenAnotherSensorRegistered_PreservesEarlierMembershipSnapshot()
    {
        var repository = new InMemorySensorRepository();
        var first = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("first-device"));
        var second = CreateSensor(DeviceIdentifier.FromUniqueIdentifier("second-device"));

        Assert.True(await repository.TryAddAsync(first));
        var previousSnapshot = await repository.GetAllAsync();

        Assert.True(await repository.TryAddAsync(second));

        Assert.Same(first, Assert.Single(previousSnapshot));
        Assert.Equal(2, (await repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task GetAllAsync_WhenCreationTimesEqual_OrdersByGuid()
    {
        var repository = new InMemorySensorRepository();

        var sensors = Enumerable.Range(0, 4)
            .Select(index => CreateSensor(DeviceIdentifier.FromUniqueIdentifier($"order-{index}")))
            .ToArray();

        foreach (var sensor in sensors)
        {
            Assert.True(await repository.TryAddAsync(sensor));
        }

        var snapshot = await repository.GetAllAsync();

        Assert.Equal(
            sensors.Select(sensor => sensor.Id).OrderBy(id => id),
            snapshot.Select(sensor => sensor.Id));
    }

    [Fact]
    public async Task TryAddAsync_WhenEquivalentMacRegistrationsRace_IndexesContainOnlyTheWinner()
    {
        string[] formats =
        [
            "AA:BB:CC:DD:EE:33",
            "aa-bb-cc-dd-ee-33",
            "AABBCCDDEE33",
            "  aa:bb:cc:dd:ee:33  "
        ];

        var repository = new InMemorySensorRepository();

        var sensors = Enumerable.Range(0, 32)
            .Select(index => CreateSensor(DeviceIdentifier.FromMacAddress(formats[index % 4])))
            .ToArray();

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Release asynchronous contenders together without sleeps or blocking
        // thread-pool threads on a barrier.
        var attempts = sensors.Select(async sensor =>
        {
            await start.Task;
            return await repository.TryAddAsync(sensor);
        }).ToArray();

        start.SetResult(true);

        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result => result));

        var winner = sensors[Array.FindIndex(results, result => result)];

        Assert.Same(winner, Assert.Single(await repository.GetAllAsync()));

        foreach (var sensor in sensors)
        {
            var byIdentifier = await repository.GetByDeviceIdentifierAsync(sensor.DeviceIdentifier);
            var byId = await repository.GetByIdAsync(sensor.Id);

            Assert.Same(winner, byIdentifier);

            if (sensor.Id == winner.Id)
            {
                Assert.Same(winner, byId);
            }
            else
            {
                Assert.Null(byId);
            }
        }
    }

    [Fact]
    public async Task TryAddAsync_WhenDistinctRegistrationsAndReadsOverlap_IndexesStayConsistent()
    {
        var repository = new InMemorySensorRepository();

        var sensors = Enumerable.Range(0, 32)
            .Select(index => CreateSensor(DeviceIdentifier.FromUniqueIdentifier($"parallel-{index}")))
            .ToArray();

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var operations = sensors.Select(async sensor =>
        {
            await start.Task;

            Assert.True(await repository.TryAddAsync(sensor));

            // Snapshot enumeration overlaps other registrations. Every visible
            // member must already be reachable through both published indexes.
            var snapshot = await repository.GetAllAsync();

            foreach (var registered in snapshot)
            {
                Assert.Same(registered, await repository.GetByIdAsync(registered.Id));
                Assert.Same(
                    registered,
                    await repository.GetByDeviceIdentifierAsync(registered.DeviceIdentifier));
            }
        }).ToArray();

        start.SetResult(true);
        await Task.WhenAll(operations);

        Assert.Equal(sensors.Length, (await repository.GetAllAsync()).Count);
    }

    private static SensorProfile CreateSensor(DeviceIdentifier deviceIdentifier)
    {
        return SensorProfile.Create(
            "Registry Test Device",
            deviceIdentifier,
            new DeploymentLocation("Facility A", "Zone 1", "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime);
    }
}