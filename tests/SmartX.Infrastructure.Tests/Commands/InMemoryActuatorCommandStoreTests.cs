using SmartX.Application.Commands;
using SmartX.Domain.Commands;
using SmartX.Infrastructure.Commands;

namespace SmartX.Infrastructure.Tests.Commands;

/// <summary>
/// Verifies the real process-local command store, per-device serialisation,
/// immutable snapshots, and failure behaviour.
/// </summary>
public sealed class InMemoryActuatorCommandStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSnapshotAsync_UncommandedDevice_StartsOff()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(deviceId, snapshot.TargetDeviceId);
        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Empty(snapshot.AuditTrail);
    }

    [Fact]
    public async Task Record_ValidCommand_UpdatesStateAndAudit()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
            {
                CommandRecord command = CreateCommand(
                    deviceId,
                    transaction.CurrentState,
                    RelayCommandType.RelayOn);

                transaction.Record(command);

                return Task.FromResult(true);
            });

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
        Assert.True(snapshot.AuditTrail[0].StateChanged);
    }

    [Fact]
    public async Task Record_NoOp_IsAuditedWithoutChangingState()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
            {
                CommandRecord command = CreateCommand(
                    deviceId,
                    transaction.CurrentState,
                    RelayCommandType.RelayOff);

                transaction.Record(command);

                return Task.FromResult(true);
            });

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.False(Assert.Single(snapshot.AuditTrail).StateChanged);
    }

    [Fact]
    public async Task GetSnapshotAsync_AfterMutation_PreviousSnapshotRemainsDetached()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot previous =
            await store.GetSnapshotAsync(deviceId);

        await RecordAsync(store, deviceId, RelayCommandType.RelayOff);

        ActuatorStateSnapshot current =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, previous.CurrentState);
        Assert.Single(previous.AuditTrail);

        Assert.Equal(RelayState.Off, current.CurrentState);
        Assert.Equal(2, current.AuditTrail.Count);

        var list = Assert.IsAssignableFrom<IList<CommandRecord>>(
            previous.AuditTrail);

        Assert.True(list.IsReadOnly);

        Assert.Throws<NotSupportedException>(
            () => list[0] = current.AuditTrail[1]);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_ConcurrentSameDevice_ObservesCompletedState()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Hold the first device transaction before applying its change.
        Task<bool> first = store.ExecuteSerializedAsync(
            deviceId,
            async (transaction, _) =>
            {
                entered.TrySetResult(true);

                await release.Task;

                transaction.Record(CreateCommand(
                    deviceId,
                    transaction.CurrentState,
                    RelayCommandType.RelayOn));

                return true;
            });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // This transaction must not enter until the first has committed.
        Task<RelayState> second = store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
                Task.FromResult(transaction.CurrentState));

        Assert.False(second.IsCompleted);

        release.TrySetResult(true);

        await first.WaitAsync(TimeSpan.FromSeconds(5));

        RelayState observed =
            await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RelayState.On, observed);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Single(snapshot.AuditTrail);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_DifferentDevices_DoNotBlockEachOther()
    {
        var store = new InMemoryActuatorCommandStore();

        Guid firstDevice = Guid.NewGuid();
        Guid secondDevice = Guid.NewGuid();

        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> blocked = store.ExecuteSerializedAsync(
            firstDevice,
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return 1;
            });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<int> independent = store.ExecuteSerializedAsync(
            secondDevice,
            (_, _) => Task.FromResult(2));

        Assert.Equal(
            2,
            await independent.WaitAsync(TimeSpan.FromSeconds(5)));

        release.TrySetResult(true);

        Assert.Equal(
            1,
            await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Record_StalePreviousState_RejectsWithoutMutation()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord stale = CreateCommand(
            deviceId,
            RelayState.On,
            RelayCommandType.RelayOff);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Record(stale);
                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Empty(snapshot.AuditTrail);
    }

    [Fact]
    public async Task Record_WrongTarget_RejectsWithoutMutation()
    {
        var store = new InMemoryActuatorCommandStore();

        Guid correctDevice = Guid.NewGuid();
        Guid otherDevice = Guid.NewGuid();

        CommandRecord wrongTarget = CreateCommand(
            otherDevice,
            RelayState.Off,
            RelayCommandType.RelayOn);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                correctDevice,
                (transaction, _) =>
                {
                    transaction.Record(wrongTarget);
                    return Task.FromResult(true);
                }));

        Assert.Empty(
            (await store.GetSnapshotAsync(correctDevice)).AuditTrail);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_FailedOperation_ReleasesDeviceGate()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExecuteSerializedAsync<int>(
                deviceId,
                (_, _) => throw new InvalidOperationException("Injected failure")));

        // A later command must still be able to obtain the same gate.
        await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
    }

    private static async Task RecordAsync(
        InMemoryActuatorCommandStore store,
        Guid deviceId,
        RelayCommandType commandType)
    {
        await store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
            {
                transaction.Record(CreateCommand(
                    deviceId,
                    transaction.CurrentState,
                    commandType));

                return Task.FromResult(true);
            });
    }

    private static CommandRecord CreateCommand(
        Guid deviceId,
        RelayState previousState,
        RelayCommandType commandType)
    {
        RelayState resultingState =
            commandType == RelayCommandType.RelayOn
                ? RelayState.On
                : RelayState.Off;

        return new CommandRecord(
            Guid.NewGuid(),
            deviceId,
            commandType,
            BaseTime,
            previousState,
            resultingState);
    }
}