using SmartX.Application.Commands;
using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Commands;

/// <summary>
/// Tests command and Undo application orchestration independently from
/// concrete infrastructure.
///
/// The probe store verifies expected service interactions and can inject
/// a failed reversal without mutating state or Undo history.
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

        Assert.Equal(1, result.UndoDepth);
        Assert.Equal(result.Command.Id, result.LatestUndoableCommandId);

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
        Assert.Equal(0, noOp.UndoDepth);
        Assert.Null(noOp.LatestUndoableCommandId);
        Assert.Single(store.Audit);

        ActuatorCommandExecutionResult changed =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        Assert.True(changed.StateChanged);
        Assert.Equal(RelayState.On, changed.CurrentState);
        Assert.Equal(1, changed.UndoDepth);
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
        Assert.Empty(store.UndoStack);
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
        Assert.Empty(store.UndoStack);
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
        Assert.Empty(store.UndoStack);
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
        Assert.Empty(store.UndoStack);
    }

    [Fact]
    public async Task UndoAsync_Success_RestoresPreviousStateAndLinksAudit()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult undone =
            await service.UndoAsync(sensor.Id, original.Command.Id);

        Assert.True(undone.StateChanged);
        Assert.True(undone.IsReversal);

        Assert.Equal(
            original.Command.Id,
            undone.Command.ReversesCommandId);

        Assert.Equal(RelayCommandType.RelayOff, undone.Command.Type);
        Assert.Equal(RelayState.On, undone.Command.PreviousState);
        Assert.Equal(RelayState.Off, undone.Command.ResultingState);
        Assert.Equal(RelayState.Off, undone.CurrentState);

        Assert.Equal(0, undone.UndoDepth);
        Assert.Null(undone.LatestUndoableCommandId);

        Assert.Equal(2, store.Audit.Count);
        Assert.Equal(original.Command.Id, store.Audit[0].Id);
        Assert.Equal(undone.Command.Id, store.Audit[1].Id);
        Assert.Empty(store.UndoStack);
    }

    [Fact]
    public async Task UndoAsync_NoOpDoesNotReplaceLatestUndoableCommand()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult noOp =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        Assert.False(noOp.StateChanged);
        Assert.Equal(1, noOp.UndoDepth);
        Assert.Equal(
            original.Command.Id,
            noOp.LatestUndoableCommandId);

        ActuatorCommandExecutionResult undone =
            await service.UndoAsync(sensor.Id, original.Command.Id);

        Assert.Equal(RelayState.Off, undone.CurrentState);
        Assert.Equal(0, undone.UndoDepth);
        Assert.Equal(3, store.Audit.Count);
    }

    [Fact]
    public async Task UndoAsync_EmptyHistory_RejectsWithoutMutation()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(sensor.Id, Guid.NewGuid()));

        Assert.Equal(RelayState.Off, store.CurrentState);
        Assert.Empty(store.Audit);
        Assert.Empty(store.UndoStack);
    }

    [Fact]
    public async Task UndoAsync_StaleCommandId_RejectsWithoutMutation()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult first =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult second =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOff);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(sensor.Id, first.Command.Id));

        Assert.Equal(RelayState.Off, store.CurrentState);
        Assert.Equal(2, store.Audit.Count);
        Assert.Equal(2, store.UndoStack.Count);
        Assert.Equal(second.Command.Id, store.UndoStack.Peek().Id);
    }

    [Fact]
    public async Task UndoAsync_UnknownTarget_RejectsWithoutMutation()
    {
        var service = CreateService(null, out ProbeCommandStore store);

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => service.UndoAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Empty(store.Audit);
        Assert.Empty(store.UndoStack);
    }

    [Fact]
    public async Task UndoAsync_EmptyExpectedCommandId_RejectsBeforeStoreAccess()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandValidationException>(
            () => service.UndoAsync(sensor.Id, Guid.Empty));

        Assert.Equal(0, store.TransactionCount);
    }

    [Fact]
    public async Task UndoAsync_EmptyTargetId_RejectsBeforeStoreAccess()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        await Assert.ThrowsAsync<ActuatorCommandValidationException>(
            () => service.UndoAsync(Guid.Empty, Guid.NewGuid()));

        Assert.Equal(0, store.TransactionCount);
    }

    [Theory]
    [InlineData(SensorStatus.Offline)]
    [InlineData(SensorStatus.Warning)]
    [InlineData(SensorStatus.Fault)]
    [InlineData(SensorStatus.Maintenance)]
    [InlineData(SensorStatus.Decommissioned)]
    public async Task UndoAsync_DeviceBecomesIneligible_LeavesStackIntact(
        SensorStatus newStatus)
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        // Simulate the operational state changing after command execution
        // but before an operator submits Undo.
        sensor.ChangeStatus(newStatus, BaseTime.AddMinutes(3));

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(sensor.Id, original.Command.Id));

        Assert.Equal(RelayState.On, store.CurrentState);
        Assert.Single(store.Audit);
        Assert.Single(store.UndoStack);
        Assert.Equal(original.Command.Id, store.UndoStack.Peek().Id);
    }

    [Fact]
    public async Task UndoAsync_InjectedReversalFailure_LeavesStackAndAuditIntact()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        store.FailNextRestore = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UndoAsync(sensor.Id, original.Command.Id));

        // The simulated failure occurs before any reversal mutation.
        Assert.Equal(RelayState.On, store.CurrentState);
        Assert.Single(store.Audit);
        Assert.Single(store.UndoStack);
        Assert.Equal(original.Command.Id, store.UndoStack.Peek().Id);

        // The failed reversal must not consume the undoable operation.
        ActuatorCommandExecutionResult retry =
            await service.UndoAsync(sensor.Id, original.Command.Id);

        Assert.Equal(RelayState.Off, retry.CurrentState);
        Assert.Empty(store.UndoStack);
        Assert.Equal(2, store.Audit.Count);
    }

    [Fact]
    public async Task UndoAsync_CancelledRequest_DoesNotConsumeStack()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UndoAsync(
                sensor.Id,
                original.Command.Id,
                cancellation.Token));

        Assert.Equal(RelayState.On, store.CurrentState);
        Assert.Single(store.Audit);
        Assert.Single(store.UndoStack);
    }

    [Fact]
    public async Task UndoAsync_ReversalCannotBeUndoneIntoToggleLoop()
    {
        SensorProfile sensor = CreateSensor();
        var service = CreateService(sensor, out ProbeCommandStore store);

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(sensor.Id, RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult reversed =
            await service.UndoAsync(sensor.Id, original.Command.Id);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(sensor.Id, reversed.Command.Id));

        Assert.Equal(RelayState.Off, store.CurrentState);
        Assert.Equal(2, store.Audit.Count);
        Assert.Empty(store.UndoStack);
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

    /// <summary>
    /// Deterministic time source for original and reversal audit metadata.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return BaseTime.AddMinutes(2).ToOffset(TimeSpan.FromHours(2));
        }
    }

    /// <summary>
    /// Minimal authoritative sensor repository double.
    ///
    /// Only the lookup path used by this service is implemented.
    /// Production index behaviour remains covered by existing tests.
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
    /// Application-level probe store.
    ///
    /// Mirrors the contract's Stack semantics so tests can inspect service
    /// behaviour directly. Concrete concurrency and atomicity are tested
    /// separately against InMemoryActuatorCommandStore.
    /// </summary>
    private sealed class ProbeCommandStore : IActuatorCommandStore
    {
        public RelayState CurrentState { get; private set; } = RelayState.Off;

        public List<CommandRecord> Audit { get; } = [];

        public Stack<CommandRecord> UndoStack { get; } = new();

        public int TransactionCount { get; private set; }

        public bool FailNextRestore { get; set; }

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

            Guid? latestId =
                UndoStack.TryPeek(out CommandRecord? latest)
                    ? latest.Id
                    : null;

            return Task.FromResult(new ActuatorStateSnapshot(
                targetDeviceId,
                CurrentState,
                Array.AsReadOnly(Audit.ToArray()),
                latestId,
                UndoStack.Count));
        }

        private sealed class ProbeTransaction : IActuatorCommandTransaction
        {
            private readonly ProbeCommandStore _store;

            public ProbeTransaction(ProbeCommandStore store)
            {
                _store = store;
            }

            public RelayState CurrentState => _store.CurrentState;

            public CommandRecord? LatestUndoableCommand =>
                _store.UndoStack.TryPeek(out CommandRecord? latest)
                    ? latest
                    : null;

            public int UndoDepth => _store.UndoStack.Count;

            public void Record(CommandRecord command)
            {
                if (command.IsReversal ||
                    command.PreviousState != _store.CurrentState)
                {
                    throw new ActuatorCommandConflictException(
                        "The manual command is not valid for this state.");
                }

                if (command.StateChanged)
                {
                    _store.UndoStack.Push(command);
                }

                _store.Audit.Add(command);
                _store.CurrentState = command.ResultingState;
            }

            public void Restore(
                CommandRecord expectedLatestCommand,
                CommandRecord reversal)
            {
                // Injected failures happen before any mutation.
                if (_store.FailNextRestore)
                {
                    _store.FailNextRestore = false;

                    throw new InvalidOperationException(
                        "Injected reversal persistence failure.");
                }

                if (!_store.UndoStack.TryPeek(out CommandRecord? latest) ||
                    latest != expectedLatestCommand ||
                    !reversal.IsReversal ||
                    reversal.ReversesCommandId != latest.Id ||
                    reversal.PreviousState != _store.CurrentState ||
                    reversal.ResultingState != latest.PreviousState)
                {
                    throw new ActuatorCommandConflictException(
                        "The reversal is invalid or stale.");
                }

                _store.Audit.Add(reversal);
                _store.CurrentState = reversal.ResultingState;

                // The reversal is audited but never pushed.
                _store.UndoStack.Pop();
            }
        }
    }
}