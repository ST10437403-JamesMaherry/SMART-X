using SmartX.Application.Commands;
using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Commands;

/// <summary>
/// Tests command eligibility and application orchestration independently
/// of infrastructure locking, which has its own dedicated tests.
/// </summary>
public sealed class ActuatorCommandServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_OnlineBooleanActuator_ChangesRelayState()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult result =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        Assert.True(result.StateChanged);
        Assert.Equal(RelayState.Off, result.Command.PreviousState);
        Assert.Equal(RelayState.On, result.Command.ResultingState);
        Assert.Equal(RelayState.On, result.CurrentState);
        Assert.Equal(sensor.Id, result.Command.TargetDeviceId);
        Assert.Equal(BaseTime.AddMinutes(2), result.Command.ExecutedAtUtc);
        Assert.Equal(SensorStatus.Online, sensor.Status);
        Assert.Single(store.Audit);
    }

    [Fact]
    public async Task ExecuteAsync_InitialRelayOff_IsSuccessfulNoOp()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult noOp =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOff);

        Assert.False(noOp.StateChanged);
        Assert.Equal(RelayState.Off, noOp.Command.PreviousState);
        Assert.Equal(RelayState.Off, noOp.CurrentState);
        Assert.Single(store.Audit);

        ActuatorCommandExecutionResult changed =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        Assert.True(changed.StateChanged);
        Assert.Equal(RelayState.On, changed.CurrentState);
        Assert.Equal(2, store.Audit.Count);
    }

    [Theory]
    [InlineData(SensorCategory.Environmental, TelemetryDataType.Boolean)]
    [InlineData(SensorCategory.PowerConsumption, TelemetryDataType.Boolean)]
    [InlineData(SensorCategory.Actuator, TelemetryDataType.Float)]
    [InlineData(SensorCategory.Actuator, TelemetryDataType.Integer)]
    public async Task ExecuteAsync_UnsupportedCategoryOrType_RejectsWithoutMutation(
        SensorCategory category,
        TelemetryDataType dataType)
    {
        SensorProfile sensor = CreateSensor(
            category: category,
            dataType: dataType);

        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn));

        Assert.Equal(RelayState.Off, store.CurrentState);
        Assert.Empty(store.Audit);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Warning)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    [InlineData(SensorStatus.Decommissioned)]
    public async Task ExecuteAsync_NonOnlineStatus_RejectsWithoutMutation(
        SensorStatus status)
    {
        SensorProfile sensor = CreateSensor(status: status);

        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn));

        Assert.Equal(RelayState.Off, store.CurrentState);
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTarget_RejectsWithoutCommand()
    {
        var service = CreateService(null, out ProbeCommandStore store);

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => service.ExecuteAsync(
                Guid.NewGuid(),
                RelayCommandType.RelayOn));

        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCommandType_RejectsBeforeStoreAccess()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandValidationException>(
            () => service.ExecuteAsync(sensor.Id, (RelayCommandType)999));

        Assert.Equal(0, store.TransactionCount);
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTargetId_RejectsBeforeStoreAccess()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandValidationException>(
            () => service.ExecuteAsync(Guid.Empty, RelayCommandType.RelayOn));

        Assert.Equal(0, store.TransactionCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledRequest_DoesNotRecordCommand()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExecuteAsync(
                sensor.Id,
                RelayCommandType.RelayOn,
                cancellation.Token));

        Assert.Empty(store.Audit);
    }

    private static ActuatorCommandService CreateService(
        SensorProfile? sensor,
        out ProbeCommandStore store)
    {
        store = new ProbeCommandStore();

        return new ActuatorCommandService(
            new StubSensorRepository(sensor),
            store,
            new FixedTimeProvider());
    }

    private static SensorProfile CreateSensor(
        SensorCategory category = SensorCategory.Actuator,
        TelemetryDataType dataType = TelemetryDataType.Boolean,
        SensorStatus status = SensorStatus.Online)
    {
        SensorProfile sensor = SensorProfile.Create(
            "Command Test Actuator",
            DeviceIdentifier.FromUniqueIdentifier("command-test-actuator"),
            new DeploymentLocation("Facility A", "Zone 1", "Relay A"),
            category,
            dataType,
            BaseTime);

        if (status != SensorStatus.Offline)
        {
            sensor.ChangeStatus(status, BaseTime.AddMinutes(1));
        }

        return sensor;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return BaseTime.AddMinutes(2).ToOffset(TimeSpan.FromHours(2));
        }
    }

    /// <summary>
    /// Minimal repository double. Production identifier indexing and
    /// concurrent repository access remain covered by existing tests.
    /// </summary>
    private sealed class StubSensorRepository : ISensorRepository
    {
        private readonly SensorProfile? _sensor;

        public StubSensorRepository(SensorProfile? sensor)
        {
            _sensor = sensor;
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                _sensor?.Id == sensorProfileId ? _sensor : null);
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Records service interactions without taking responsibility for
    /// concurrency. The real Infrastructure store tests the device gates.
    /// </summary>
    private sealed class ProbeCommandStore : IActuatorCommandStore
    {
        public RelayState CurrentState { get; private set; } = RelayState.Off;

        public List<CommandRecord> Audit { get; } = [];

        public int TransactionCount { get; private set; }

        public Task<TResult> ExecuteSerializedAsync<TResult>(
            Guid targetDeviceId,
            Func<IActuatorCommandTransaction, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TransactionCount++;

            return operation(new ProbeTransaction(this), cancellationToken);
        }

        public Task<ActuatorStateSnapshot> GetSnapshotAsync(
            Guid targetDeviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new ActuatorStateSnapshot(
                targetDeviceId,
                CurrentState,
                Array.AsReadOnly(Audit.ToArray())));
        }

        private sealed class ProbeTransaction : IActuatorCommandTransaction
        {
            private readonly ProbeCommandStore _store;

            public ProbeTransaction(ProbeCommandStore store)
            {
                _store = store;
            }

            public RelayState CurrentState => _store.CurrentState;

            public void Record(CommandRecord command)
            {
                _store.Audit.Add(command);
                _store.CurrentState = command.ResultingState;
            }
        }
    }
}