using SmartX.Application.Commands;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Commands;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Realtime;

/// <summary>
/// Verifies authoritative real-time projections and compatibility with
/// the original registered-sensor repository.
///
/// Commanded actuator state is projected separately from operational
/// sensor status and the latest processed telemetry observation.
/// </summary>
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

        Assert.True(await repository.TryAddAsync(
            CreateSensor("AA:BB:CC:DD:EE:62")));

        var current = await service.GetSnapshotAsync();
        var oldDevice = Assert.Single(previous.Devices);
        var updated = Assert.Single(
            current.Devices,
            device => device.Id == sensor.Id);

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
        Assert.Null(updated.CommandedActuator);
    }

    [Fact]
    public async Task GetDevicesAsync_WhenReturned_CollectionRejectsElementReplacement()
    {
        var repository = new RecordingSensorRepository(
            CreateSensor("AA:BB:CC:DD:EE:63"));

        var service = CreateService(repository);

        var devices = await service.GetDevicesAsync();

        var writableView =
            Assert.IsAssignableFrom<IList<RealtimeDeviceResponse>>(
                devices);

        Assert.True(writableView.IsReadOnly);

        Assert.Throws<NotSupportedException>(
            () => writableView[0] = devices[0] with
            {
                Name = "Overwritten"
            });

        Assert.Equal(
            "Registry Device",
            Assert.Single(await service.GetDevicesAsync()).Name);
    }

    [Fact]
    public async Task GetByDeviceIdentifierAsync_WhenMacEquivalent_UsesOnlyExactRepositoryLookup()
    {
        var sensor = CreateSensor("AA:BB:CC:DD:EE:64");
        var repository = new RecordingSensorRepository(sensor);
        var service = CreateService(repository);

        var device = await service.GetByDeviceIdentifierAsync(
            "aabbccddee64",
            "macaddress");

        Assert.Equal(sensor.Id, device.Id);
        Assert.Equal("AA:BB:CC:DD:EE:64", device.DeviceIdentifier);
        Assert.Equal("MacAddress", device.IdentifierKind);
        Assert.Equal(1, repository.IdentifierLookupCount);
        Assert.Equal(0, repository.GetAllCount);
        Assert.Null(device.CommandedActuator);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelled_DoesNotReadRepository()
    {
        var repository = new RecordingSensorRepository();
        var service = CreateService(repository);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetSnapshotAsync(
                cancellationSource.Token));

        Assert.Equal(0, repository.GetAllCount);
        Assert.Equal(0, repository.IdentifierLookupCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ActuatorWithoutCommands_HasExplicitOffState()
    {
        SensorProfile actuator = CreateActuator("AA:BB:CC:DD:EE:65");

        var repository = new RecordingSensorRepository(actuator);
        var commandStore = new RecordingActuatorCommandStore();

        var service = CreateService(repository, commandStore);

        RealtimeDeviceResponse device =
            Assert.Single((await service.GetSnapshotAsync()).Devices);

        Assert.Equal("Actuator", device.Category);
        Assert.Equal("Boolean", device.TelemetryDataType);
        Assert.Equal("Offline", device.Status);

        Assert.NotNull(device.CommandedActuator);
        Assert.Equal(
            RelayState.Off.ToString(),
            device.CommandedActuator.CommandedState);

        Assert.Equal(0, device.CommandedActuator.UndoDepth);
        Assert.Null(
            device.CommandedActuator.LatestUndoableCommandId);

        Assert.Null(device.LatestProcessedTelemetry);
        Assert.Null(device.LastProcessedAtUtc);

        Assert.Equal(1, commandStore.ReadCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ActuatorWithHistory_ProjectsActualStackMetadata()
    {
        SensorProfile actuator = CreateActuator("AA:BB:CC:DD:EE:66");

        actuator.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(-1));

        CommandRecord original = new(
            Guid.NewGuid(),
            actuator.Id,
            RelayCommandType.RelayOn,
            BaseTime,
            RelayState.Off,
            RelayState.On);

        var commandStore = new RecordingActuatorCommandStore();

        commandStore.SetSnapshot(new ActuatorStateSnapshot(
            actuator.Id,
            RelayState.On,
            Array.AsReadOnly(new[] { original }),
            original.Id,
            1));

        var repository = new RecordingSensorRepository(actuator);
        var service = CreateService(repository, commandStore);

        RealtimeDeviceResponse device =
            Assert.Single((await service.GetSnapshotAsync()).Devices);

        Assert.Equal("Online", device.Status);
        Assert.NotNull(device.CommandedActuator);

        Assert.Equal("On", device.CommandedActuator.CommandedState);
        Assert.Equal(1, device.CommandedActuator.UndoDepth);
        Assert.Equal(
            original.Id,
            device.CommandedActuator.LatestUndoableCommandId);

        Assert.Null(device.LatestProcessedTelemetry);

        var history = await service.GetActuatorHistoryAsync(actuator.Id);

        Assert.Equal("On", history.Actuator.CommandedState);
        Assert.Equal(1, history.TotalAuditCount);
        Assert.Equal(1, history.Actuator.UndoDepth);

        var audited = Assert.Single(history.RecentAudit);

        Assert.Equal(original.Id, audited.CommandId);
        Assert.Equal("Off", audited.PreviousState);
        Assert.Equal("On", audited.ResultingState);
        Assert.True(audited.StateChanged);
        Assert.False(audited.IsReversal);
    }

    [Fact]
    public async Task GetActuatorHistoryAsync_NonActuator_RejectsWithoutReadingCommandStore()
    {
        SensorProfile sensor = CreateSensor("AA:BB:CC:DD:EE:67");

        var commandStore = new RecordingActuatorCommandStore();

        var service = CreateService(
            new RecordingSensorRepository(sensor),
            commandStore);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.GetActuatorHistoryAsync(sensor.Id));

        Assert.Equal(0, commandStore.ReadCount);
    }

    [Fact]
    public async Task GetActuatorHistoryAsync_UnknownTarget_ReturnsSensorNotFound()
    {
        var service = CreateService(
            new RecordingSensorRepository());

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => service.GetActuatorHistoryAsync(Guid.NewGuid()));
    }

    private static RealtimeSnapshotService CreateService(
        ISensorRepository repository,
        IActuatorCommandStore? commandStore = null)
    {
        return new RealtimeSnapshotService(
            repository,
            new FixedTimeProvider(),
            new EmptyProcessingStore(),
            commandStore ?? new RecordingActuatorCommandStore());
    }

    private static SensorProfile CreateSensor(string macAddress)
    {
        return SensorProfile.Create(
            "Registry Device",
            DeviceIdentifier.FromMacAddress(macAddress),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime.AddMinutes(-5));
    }

    private static SensorProfile CreateActuator(string macAddress)
    {
        return SensorProfile.Create(
            "Registry Actuator",
            DeviceIdentifier.FromMacAddress(macAddress),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Relay 1"),
            SensorCategory.Actuator,
            TelemetryDataType.Boolean,
            BaseTime.AddMinutes(-5));
    }

    /// <summary>
    /// Simulates a processing store with no completed telemetry.
    /// Its remaining operations are unrelated to read projections.
    /// </summary>
    private sealed class EmptyProcessingStore : ITelemetryProcessingStore
    {
        public TelemetryDeviceProcessingState? GetDeviceState(Guid id)
        {
            return null;
        }

        public TelemetryProcessingSnapshot GetSnapshot()
        {
            return new TelemetryProcessingSnapshot(
                new TelemetryWorkBufferSnapshot(
                    0,
                    0,
                    false,
                    [],
                    []),
                0,
                TelemetryWorkerStatus.Waiting,
                null,
                null,
                []);
        }

        public void MarkWaiting()
        {
            throw new NotSupportedException();
        }

        public void Begin(TelemetryDispatch dispatch, DateTimeOffset at)
        {
            throw new NotSupportedException();
        }

        public void Complete(TelemetryDispatch dispatch, DateTimeOffset at)
        {
            throw new NotSupportedException();
        }

        public void MarkFaulted(DateTimeOffset at)
        {
            throw new NotSupportedException();
        }

        public void MarkStopped(DateTimeOffset at)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Supplies detached command snapshots without mutating real state.
    /// The command service and concrete Stack store are covered by
    /// their own dedicated tests.
    /// </summary>
    private sealed class RecordingActuatorCommandStore : IActuatorCommandStore
    {
        private readonly Dictionary<Guid, ActuatorStateSnapshot> _snapshots =
            new();

        public int ReadCount { get; private set; }

        public void SetSnapshot(ActuatorStateSnapshot snapshot)
        {
            _snapshots[snapshot.TargetDeviceId] = snapshot;
        }

        public Task<ActuatorStateSnapshot> GetSnapshotAsync(
            Guid targetDeviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadCount++;

            if (_snapshots.TryGetValue(
                targetDeviceId,
                out ActuatorStateSnapshot? snapshot))
            {
                return Task.FromResult(snapshot);
            }

            return Task.FromResult(new ActuatorStateSnapshot(
                targetDeviceId,
                RelayState.Off,
                Array.AsReadOnly(Array.Empty<CommandRecord>()),
                null,
                0));
        }

        public Task<TResult> ExecuteSerializedAsync<TResult>(
            Guid targetDeviceId,
            Func<IActuatorCommandTransaction, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "This test double supports read projections only.");
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            // The snapshot service must normalise this to UTC.
            return BaseTime.ToOffset(TimeSpan.FromHours(2));
        }
    }

    /// <summary>
    /// Isolates repository read-service orchestration.
    /// The actual repository's locking and canonical Dictionary lookup
    /// are covered in the Infrastructure and API integration tests.
    /// </summary>
    private sealed class RecordingSensorRepository : ISensorRepository
    {
        private readonly Dictionary<Guid, SensorProfile> _sensors = new();

        private readonly Dictionary<DeviceIdentifier, SensorProfile> _identifiers =
            new();

        public RecordingSensorRepository(params SensorProfile[] sensors)
        {
            foreach (SensorProfile sensor in sensors)
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

            if (_sensors.ContainsKey(sensor.Id) ||
                _identifiers.ContainsKey(sensor.DeviceIdentifier))
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
                _sensors.Values
                    .OrderBy(sensor => sensor.CreatedAtUtc)
                    .ThenBy(sensor => sensor.Id)
                    .ToArray());
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _sensors.TryGetValue(
                sensorProfileId,
                out SensorProfile? sensor);

            return Task.FromResult(sensor);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IdentifierLookupCount++;

            _identifiers.TryGetValue(
                deviceIdentifier,
                out SensorProfile? sensor);

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sensors.TryGetValue(sensor.Id, out SensorProfile? existing) ||
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