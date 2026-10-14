using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Realtime;

public sealed class RealtimeSnapshotServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSnapshotAsync_WhenSourceChanges_PreviousResponseRemainsDetached()
    {
        var sensor = CreateSensor("AA:BB:CC:DD:EE:61");
        var repository = new RecordingSensorRepository(sensor);
        var service = CreateService(repository);

        var previous = await service.GetSnapshotAsync();

        sensor.UpdateDetails(
            "Updated Device",
            new DeploymentLocation("Facility B", "Zone 2", "Node 2"),
            SensorCategory.PowerConsumption,
            TelemetryDataType.Integer,
            BaseTime.AddMinutes(-1));

        sensor.ChangeStatus(SensorStatus.Warning, BaseTime);

        Assert.True(await repository.TryAddAsync(CreateSensor("AA:BB:CC:DD:EE:62")));

        var current = await service.GetSnapshotAsync();
        var oldDevice = Assert.Single(previous.Devices);
        var updated = Assert.Single(current.Devices, device => device.Id == sensor.Id);

        Assert.Equal(1, previous.DeviceCount);
        Assert.Equal(2, current.DeviceCount);
        Assert.Equal("Registry Device", oldDevice.Name);
        Assert.Equal("Facility A", oldDevice.DeploymentLocation.Facility);
        Assert.Equal("Offline", oldDevice.Status);
        Assert.Equal("Float", oldDevice.TelemetryDataType);
        Assert.Equal("Updated Device", updated.Name);
        Assert.Equal("Facility B", updated.DeploymentLocation.Facility);
        Assert.Equal("Warning", updated.Status);
        Assert.Equal("Integer", updated.TelemetryDataType);
        Assert.Equal(BaseTime, current.GeneratedAtUtc);
        Assert.Equal(TimeSpan.Zero, current.GeneratedAtUtc.Offset);
        Assert.Null(updated.LatestProcessedTelemetry);
        Assert.Null(updated.LastProcessedAtUtc);
    }

    [Fact]
    public async Task GetDevicesAsync_WhenReturned_CollectionRejectsElementReplacement()
    {
        var repository = new RecordingSensorRepository(CreateSensor("AA:BB:CC:DD:EE:63"));
        var service = CreateService(repository);

        var devices = await service.GetDevicesAsync();
        var writableView = Assert.IsAssignableFrom<IList<RealtimeDeviceResponse>>(devices);

        Assert.True(writableView.IsReadOnly);

        Assert.Throws<NotSupportedException>(
            () => writableView[0] = devices[0] with { Name = "Overwritten" });

        Assert.Equal("Registry Device", Assert.Single(await service.GetDevicesAsync()).Name);
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenMacEquivalent_UsesOnlyExactRepositoryLookup()
    {
        var sensor = CreateSensor("AA:BB:CC:DD:EE:64");
        var repository = new RecordingSensorRepository(sensor);
        var service = CreateService(repository);

        var device = await service.GetByDeviceIdentifierAsync("aabbccddee64", "macaddress");

        Assert.Equal(sensor.Id, device.Id);
        Assert.Equal("AA:BB:CC:DD:EE:64", device.DeviceIdentifier);
        Assert.Equal("MacAddress", device.IdentifierKind);
        Assert.Equal(1, repository.IdentifierLookupCount);
        Assert.Equal(0, repository.GetAllCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelled_DoesNotReadRepository()
    {
        var repository = new RecordingSensorRepository();
        var service = CreateService(repository);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetSnapshotAsync(cancellationSource.Token));

        Assert.Equal(0, repository.GetAllCount);
        Assert.Equal(0, repository.IdentifierLookupCount);
    }

    private static RealtimeSnapshotService CreateService(ISensorRepository repository)
    {
        return new RealtimeSnapshotService(repository, new FixedTimeProvider(), new EmptyProcessingStore());
    }

    private static SensorProfile CreateSensor(string macAddress)
    {
        return SensorProfile.Create(
            "Registry Device",
            DeviceIdentifier.FromMacAddress(macAddress),
            new DeploymentLocation("Facility A", "Zone 1", "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime.AddMinutes(-5));
    }

    private sealed class EmptyProcessingStore : ITelemetryProcessingStore
    {
        public TelemetryDeviceProcessingState? GetDeviceState(Guid id) => null;
        public TelemetryProcessingSnapshot GetSnapshot() => new(
            new TelemetryWorkBufferSnapshot(0, 0, false, [], []), 0,
            TelemetryWorkerStatus.Waiting, null, null, []);
        public void MarkWaiting() => throw new NotSupportedException();
        public void Begin(TelemetryDispatch dispatch, DateTimeOffset at) => throw new NotSupportedException();
        public void Complete(TelemetryDispatch dispatch, DateTimeOffset at) => throw new NotSupportedException();
        public void MarkFaulted(DateTimeOffset at) => throw new NotSupportedException();
        public void MarkStopped(DateTimeOffset at) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            // The service must normalise the supplied instant to UTC.
            return BaseTime.ToOffset(TimeSpan.FromHours(2));
        }
    }

    /// <summary>
    /// Isolates read-service orchestration. The actual repository's lock and
    /// paired-index concurrency are covered in Infrastructure tests.
    /// </summary>
    private sealed class RecordingSensorRepository : ISensorRepository
    {
        private readonly Dictionary<Guid, SensorProfile> _sensors = new();
        private readonly Dictionary<DeviceIdentifier, SensorProfile> _identifiers = new();

        public RecordingSensorRepository(params SensorProfile[] sensors)
        {
            foreach (var sensor in sensors)
            {
                _sensors.Add(sensor.Id, sensor);
                _identifiers.Add(sensor.DeviceIdentifier, sensor);
            }
        }

        public int GetAllCount { get; private set; }
        public int IdentifierLookupCount { get; private set; }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_sensors.ContainsKey(sensor.Id) || _identifiers.ContainsKey(sensor.DeviceIdentifier))
            {
                return Task.FromResult(false);
            }

            _sensors.Add(sensor.Id, sensor);
            _identifiers.Add(sensor.DeviceIdentifier, sensor);

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetAllCount++;

            return Task.FromResult<IReadOnlyList<SensorProfile>>(
                _sensors.Values.OrderBy(sensor => sensor.CreatedAtUtc)
                    .ThenBy(sensor => sensor.Id).ToArray());
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sensors.TryGetValue(sensorProfileId, out var sensor);

            return Task.FromResult(sensor);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentifierLookupCount++;
            _identifiers.TryGetValue(deviceIdentifier, out var sensor);

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sensors.TryGetValue(sensor.Id, out var existing) ||
                existing.DeviceIdentifier != sensor.DeviceIdentifier)
            {
                return Task.FromResult(false);
            }

            _sensors[sensor.Id] = sensor;
            _identifiers[sensor.DeviceIdentifier] = sensor;

            return Task.FromResult(true);
        }
    }
}