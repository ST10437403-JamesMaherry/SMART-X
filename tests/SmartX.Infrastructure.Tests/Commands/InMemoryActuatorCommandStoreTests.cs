using SmartX.Application.Commands;
using SmartX.Domain.Commands;
using SmartX.Infrastructure.Commands;

namespace SmartX.Infrastructure.Tests.Commands;

/// <summary>
/// Verifies the real process-local actuator store, per-device serialisation,
/// Stack-backed Undo, detached snapshots, and failure preservation.
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
        Assert.Null(snapshot.LatestUndoableCommandId);
        Assert.Equal(0, snapshot.UndoDepth);
    }

    [Fact]
    public async Task Record_ValidCommand_UpdatesStateAuditAndStack()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
        Assert.True(snapshot.AuditTrail[0].StateChanged);
        Assert.Equal(1, snapshot.UndoDepth);
        Assert.Equal(
            snapshot.AuditTrail[0].Id,
            snapshot.LatestUndoableCommandId);
    }

    [Fact]
    public async Task Record_NoOp_IsAuditedWithoutChangingStack()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await RecordAsync(store, deviceId, RelayCommandType.RelayOff);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.False(Assert.Single(snapshot.AuditTrail).StateChanged);
        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Null(snapshot.LatestUndoableCommandId);
    }

    [Fact]
    public async Task Record_NoOpAfterChange_PreservesLatestUndoableCommand()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord original =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Equal(2, snapshot.AuditTrail.Count);
        Assert.Equal(1, snapshot.UndoDepth);
        Assert.Equal(original.Id, snapshot.LatestUndoableCommandId);
    }

    [Fact]
    public async Task Record_MultipleChanges_UsesLatestCommandAsStackTop()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord first =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        CommandRecord second =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOff);

        CommandRecord third =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Equal(3, snapshot.UndoDepth);
        Assert.Equal(third.Id, snapshot.LatestUndoableCommandId);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(second.Id, third.Id);
    }

    [Fact]
    public async Task Restore_ValidLatestCommand_RestoresAndRetainsAudit()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord original =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        CommandRecord reversal =
            await RestoreLatestAsync(store, deviceId);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Null(snapshot.LatestUndoableCommandId);

        Assert.Equal(2, snapshot.AuditTrail.Count);
        Assert.Equal(original.Id, snapshot.AuditTrail[0].Id);
        Assert.Equal(reversal.Id, snapshot.AuditTrail[1].Id);

        Assert.True(snapshot.AuditTrail[1].IsReversal);
        Assert.Equal(
            original.Id,
            snapshot.AuditTrail[1].ReversesCommandId);
    }

    [Fact]
    public async Task Restore_MultipleChanges_UsesLifoOrdering()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord first =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        CommandRecord second =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOff);

        CommandRecord third =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        CommandRecord undoThird =
            await RestoreLatestAsync(store, deviceId);

        ActuatorStateSnapshot afterFirstUndo =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(third.Id, undoThird.ReversesCommandId);
        Assert.Equal(RelayState.Off, afterFirstUndo.CurrentState);
        Assert.Equal(2, afterFirstUndo.UndoDepth);
        Assert.Equal(second.Id, afterFirstUndo.LatestUndoableCommandId);

        CommandRecord undoSecond =
            await RestoreLatestAsync(store, deviceId);

        ActuatorStateSnapshot afterSecondUndo =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(second.Id, undoSecond.ReversesCommandId);
        Assert.Equal(RelayState.On, afterSecondUndo.CurrentState);
        Assert.Equal(1, afterSecondUndo.UndoDepth);
        Assert.Equal(first.Id, afterSecondUndo.LatestUndoableCommandId);

        CommandRecord undoFirst =
            await RestoreLatestAsync(store, deviceId);

        ActuatorStateSnapshot finished =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(first.Id, undoFirst.ReversesCommandId);
        Assert.Equal(RelayState.Off, finished.CurrentState);
        Assert.Equal(0, finished.UndoDepth);
        Assert.Equal(6, finished.AuditTrail.Count);
    }

    [Fact]
    public async Task Restore_StaleExpectedCommand_LeavesStackAndAuditIntact()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord first =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        CommandRecord second =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOff);

        CommandRecord reversalOfFirst = CreateReversal(first);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Restore(first, reversalOfFirst);

                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(2, snapshot.UndoDepth);
        Assert.Equal(second.Id, snapshot.LatestUndoableCommandId);
        Assert.Equal(2, snapshot.AuditTrail.Count);
    }

    [Fact]
    public async Task Restore_InvalidReversalLink_LeavesStackAndAuditIntact()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord original =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        // State values are correct, but the reversal references the
        // wrong command ID. This must not remove the Stack entry.
        CommandRecord invalidReversal = new(
            Guid.NewGuid(),
            deviceId,
            RelayCommandType.RelayOff,
            BaseTime.AddMinutes(1),
            RelayState.On,
            RelayState.Off,
            Guid.NewGuid());

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Restore(original, invalidReversal);

                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Equal(1, snapshot.UndoDepth);
        Assert.Equal(original.Id, snapshot.LatestUndoableCommandId);
        Assert.Single(snapshot.AuditTrail);

        // A valid reversal must still work after the failed attempt.
        await RestoreLatestAsync(store, deviceId);

        ActuatorStateSnapshot restored =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, restored.CurrentState);
        Assert.Equal(0, restored.UndoDepth);
    }

    [Fact]
    public async Task Restore_IncorrectResultingState_RejectsWithoutMutation()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord original =
            await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        // This would leave the relay On instead of restoring Off.
        CommandRecord invalidReversal = new(
            Guid.NewGuid(),
            deviceId,
            RelayCommandType.RelayOn,
            BaseTime.AddMinutes(1),
            RelayState.On,
            RelayState.On,
            original.Id);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Restore(original, invalidReversal);

                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
        Assert.Equal(1, snapshot.UndoDepth);
        Assert.Equal(original.Id, snapshot.LatestUndoableCommandId);
    }

    [Fact]
    public async Task Restore_EmptyStack_RejectsWithoutAudit()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord fabricated = CreateCommand(
            deviceId,
            RelayState.Off,
            RelayCommandType.RelayOn);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Restore(
                        fabricated,
                        CreateReversal(fabricated));

                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Empty(snapshot.AuditTrail);
        Assert.Equal(0, snapshot.UndoDepth);
    }

    [Fact]
    public async Task Record_ReversalCannotBeAddedAsManualUndoableCommand()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        CommandRecord original = CreateCommand(
            deviceId,
            RelayState.Off,
            RelayCommandType.RelayOn);

        CommandRecord reversal = CreateReversal(original);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => store.ExecuteSerializedAsync(
                deviceId,
                (transaction, _) =>
                {
                    transaction.Record(reversal);

                    return Task.FromResult(true);
                }));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Empty(snapshot.AuditTrail);
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
        Assert.Equal(1, previous.UndoDepth);

        Assert.Equal(RelayState.Off, current.CurrentState);
        Assert.Equal(2, current.AuditTrail.Count);
        Assert.Equal(2, current.UndoDepth);

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
        Assert.Single(
            (await store.GetSnapshotAsync(deviceId)).AuditTrail);
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
        Assert.Equal(0, snapshot.UndoDepth);
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

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(correctDevice);

        Assert.Empty(snapshot.AuditTrail);
        Assert.Equal(0, snapshot.UndoDepth);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_FailedOperation_ReleasesDeviceGate()
    {
        var store = new InMemoryActuatorCommandStore();
        Guid deviceId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExecuteSerializedAsync<int>(
                deviceId,
                (_, _) => throw new InvalidOperationException(
                    "Injected failure")));

        // The gate must remain usable after a failed operation.
        await RecordAsync(store, deviceId, RelayCommandType.RelayOn);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(deviceId);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
        Assert.Equal(1, snapshot.UndoDepth);
    }

    /// <summary>
    /// Applies a valid manual command through the real store transaction.
    /// </summary>
    private static Task<CommandRecord> RecordAsync(
        InMemoryActuatorCommandStore store,
        Guid deviceId,
        RelayCommandType commandType)
    {
        return store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
            {
                CommandRecord command = CreateCommand(
                    deviceId,
                    transaction.CurrentState,
                    commandType);

                transaction.Record(command);

                return Task.FromResult(command);
            });
    }

    /// <summary>
    /// Executes a valid reversal using the real current Stack top.
    /// </summary>
    private static Task<CommandRecord> RestoreLatestAsync(
        InMemoryActuatorCommandStore store,
        Guid deviceId)
    {
        return store.ExecuteSerializedAsync(
            deviceId,
            (transaction, _) =>
            {
                CommandRecord original =
                    Assert.IsType<CommandRecord>(
                        transaction.LatestUndoableCommand);

                CommandRecord reversal = CreateReversal(original);

                transaction.Restore(original, reversal);

                return Task.FromResult(reversal);
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

    private static CommandRecord CreateReversal(CommandRecord original)
    {
        RelayCommandType reversalType =
            original.PreviousState == RelayState.On
                ? RelayCommandType.RelayOn
                : RelayCommandType.RelayOff;

        return new CommandRecord(
            Guid.NewGuid(),
            original.TargetDeviceId,
            reversalType,
            BaseTime.AddMinutes(1),
            original.ResultingState,
            original.PreviousState,
            original.Id);
    }
}