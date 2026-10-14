using SmartX.Application.Commands;
using SmartX.Application.Sensors;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;
using SmartX.Infrastructure.Commands;
using SmartX.Infrastructure.Sensors;

namespace SmartX.Infrastructure.Tests.Commands;

/// <summary>
/// Exercises the complete in-process command workflow using:
///
/// - The authoritative registered sensor repository.
/// - The real application command service.
/// - The real per-device Stack-backed actuator store.
///
/// These tests verify that observed command results reflect actual
/// commanded state rather than a simulated UI-only history.
/// </summary>
public sealed class ActuatorCommandUndoIntegrationTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultipleCommands_UndoInLifoOrder_RestoresOriginalState()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "lifo-relay");

        // Initial state: Off.
        ActuatorStateSnapshot initial =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.Off, initial.CurrentState);
        Assert.Equal(0, initial.UndoDepth);

        // Off -> On -> Off -> On.
        ActuatorCommandExecutionResult first =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult second =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOff);

        ActuatorCommandExecutionResult third =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        ActuatorStateSnapshot beforeUndo =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.On, beforeUndo.CurrentState);
        Assert.Equal(3, beforeUndo.UndoDepth);
        Assert.Equal(
            third.Command.Id,
            beforeUndo.LatestUndoableCommandId);

        // Undo command 3: On -> Off.
        ActuatorCommandExecutionResult undoThird =
            await service.UndoAsync(device.Id, third.Command.Id);

        Assert.Equal(RelayState.Off, undoThird.CurrentState);
        Assert.Equal(2, undoThird.UndoDepth);
        Assert.Equal(
            second.Command.Id,
            undoThird.LatestUndoableCommandId);

        // Undo command 2: Off -> On.
        ActuatorCommandExecutionResult undoSecond =
            await service.UndoAsync(device.Id, second.Command.Id);

        Assert.Equal(RelayState.On, undoSecond.CurrentState);
        Assert.Equal(1, undoSecond.UndoDepth);
        Assert.Equal(
            first.Command.Id,
            undoSecond.LatestUndoableCommandId);

        // Undo command 1: On -> Off.
        ActuatorCommandExecutionResult undoFirst =
            await service.UndoAsync(device.Id, first.Command.Id);

        Assert.Equal(RelayState.Off, undoFirst.CurrentState);
        Assert.Equal(0, undoFirst.UndoDepth);
        Assert.Null(undoFirst.LatestUndoableCommandId);

        ActuatorStateSnapshot final =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.Off, final.CurrentState);
        Assert.Equal(0, final.UndoDepth);

        // Three originals and three reversals remain in the audit.
        Assert.Equal(6, final.AuditTrail.Count);

        Assert.Equal(first.Command.Id, final.AuditTrail[0].Id);
        Assert.Equal(second.Command.Id, final.AuditTrail[1].Id);
        Assert.Equal(third.Command.Id, final.AuditTrail[2].Id);

        Assert.Equal(
            third.Command.Id,
            final.AuditTrail[3].ReversesCommandId);

        Assert.Equal(
            second.Command.Id,
            final.AuditTrail[4].ReversesCommandId);

        Assert.Equal(
            first.Command.Id,
            final.AuditTrail[5].ReversesCommandId);

        // Commanded relay state does not modify operational health.
        Assert.Equal(SensorStatus.Online, device.Status);
    }

    [Fact]
    public async Task MultipleDevices_UndoIsIsolatedPerTarget()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile firstDevice =
            await RegisterOnlineActuatorAsync(repository, "relay-a");

        SensorProfile secondDevice =
            await RegisterOnlineActuatorAsync(repository, "relay-b");

        await service.ExecuteAsync(
            firstDevice.Id,
            RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult secondCommand =
            await service.ExecuteAsync(
                secondDevice.Id,
                RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult firstDeviceOff =
            await service.ExecuteAsync(
                firstDevice.Id,
                RelayCommandType.RelayOff);

        // Undo must affect the selected first device only.
        ActuatorCommandExecutionResult restored =
            await service.UndoAsync(
                firstDevice.Id,
                firstDeviceOff.Command.Id);

        Assert.Equal(RelayState.On, restored.CurrentState);

        ActuatorStateSnapshot firstSnapshot =
            await store.GetSnapshotAsync(firstDevice.Id);

        ActuatorStateSnapshot secondSnapshot =
            await store.GetSnapshotAsync(secondDevice.Id);

        Assert.Equal(RelayState.On, firstSnapshot.CurrentState);
        Assert.Equal(RelayState.On, secondSnapshot.CurrentState);

        Assert.Equal(1, firstSnapshot.UndoDepth);
        Assert.Equal(1, secondSnapshot.UndoDepth);

        Assert.Equal(
            secondCommand.Command.Id,
            secondSnapshot.LatestUndoableCommandId);

        Assert.Single(secondSnapshot.AuditTrail);
    }

    [Fact]
    public async Task NoOpAfterStateChange_DoesNotConsumeUndoHistory()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "noop-relay");

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult noOp =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        Assert.False(noOp.StateChanged);
        Assert.Equal(1, noOp.UndoDepth);
        Assert.Equal(
            original.Command.Id,
            noOp.LatestUndoableCommandId);

        ActuatorCommandExecutionResult reversal =
            await service.UndoAsync(
                device.Id,
                original.Command.Id);

        Assert.Equal(RelayState.Off, reversal.CurrentState);

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Equal(3, snapshot.AuditTrail.Count);
    }

    [Fact]
    public async Task StaleUndo_RejectsWithoutChangingCurrentDeviceState()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "stale-relay");

        ActuatorCommandExecutionResult first =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult second =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOff);

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(device.Id, first.Command.Id));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(2, snapshot.UndoDepth);
        Assert.Equal(2, snapshot.AuditTrail.Count);

        Assert.Equal(
            second.Command.Id,
            snapshot.LatestUndoableCommandId);
    }

    [Fact]
    public async Task DeviceBecomesMaintenance_UndoRevalidatesEligibility()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "maintenance-relay");

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        // The repository profile remains authoritative.
        device.ChangeStatus(
            SensorStatus.Maintenance,
            BaseTime.AddMinutes(4));

        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(
                device.Id,
                original.Command.Id));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.On, snapshot.CurrentState);
        Assert.Single(snapshot.AuditTrail);
        Assert.Equal(1, snapshot.UndoDepth);
        Assert.Equal(
            original.Command.Id,
            snapshot.LatestUndoableCommandId);

        Assert.Equal(SensorStatus.Maintenance, device.Status);
    }

    [Fact]
    public async Task ConcurrentUndo_SameExpectedCommand_SucceedsExactlyOnce()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "concurrent-relay");

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        // Both requests attempt to undo the same expected Stack entry.
        // The per-device SemaphoreSlim must prevent double removal.
        Task<Exception?> first = Task.Run(
            () => AttemptUndoAsync(
                service,
                device.Id,
                original.Command.Id));

        Task<Exception?> second = Task.Run(
            () => AttemptUndoAsync(
                service,
                device.Id,
                original.Command.Id));

        Exception?[] outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(result => result is null));

        Assert.Equal(
            1,
            outcomes.Count(
                result => result is ActuatorCommandConflictException));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(0, snapshot.UndoDepth);

        // Only one reversal was applied.
        Assert.Equal(2, snapshot.AuditTrail.Count);
        Assert.Equal(original.Command.Id, snapshot.AuditTrail[0].Id);

        Assert.Equal(
            original.Command.Id,
            snapshot.AuditTrail[1].ReversesCommandId);
    }

    [Fact]
    public async Task ReversalAudit_DoesNotAllowInfiniteUndoLoop()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        SensorProfile device =
            await RegisterOnlineActuatorAsync(repository, "reversal-relay");

        ActuatorCommandExecutionResult original =
            await service.ExecuteAsync(
                device.Id,
                RelayCommandType.RelayOn);

        ActuatorCommandExecutionResult reversal =
            await service.UndoAsync(
                device.Id,
                original.Command.Id);

        // The reversal itself is not undoable.
        await Assert.ThrowsAsync<ActuatorCommandConflictException>(
            () => service.UndoAsync(
                device.Id,
                reversal.Command.Id));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(device.Id);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Equal(2, snapshot.AuditTrail.Count);
    }

    [Fact]
    public async Task UnknownTarget_RejectsCommandAndUndo()
    {
        var repository = new InMemorySensorRepository();
        var store = new InMemoryActuatorCommandStore();

        ActuatorCommandService service = CreateService(repository, store);

        Guid unknownDeviceId = Guid.NewGuid();

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => service.ExecuteAsync(
                unknownDeviceId,
                RelayCommandType.RelayOn));

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => service.UndoAsync(
                unknownDeviceId,
                Guid.NewGuid()));

        ActuatorStateSnapshot snapshot =
            await store.GetSnapshotAsync(unknownDeviceId);

        Assert.Equal(RelayState.Off, snapshot.CurrentState);
        Assert.Equal(0, snapshot.UndoDepth);
        Assert.Empty(snapshot.AuditTrail);
    }

    private static ActuatorCommandService CreateService(
        InMemorySensorRepository repository,
        InMemoryActuatorCommandStore store)
    {
        return new ActuatorCommandService(
            repository,
            store,
            new FixedTimeProvider());
    }

    private static async Task<SensorProfile> RegisterOnlineActuatorAsync(
        InMemorySensorRepository repository,
        string identifier)
    {
        SensorProfile sensor = SensorProfile.Create(
            $"Test Actuator {identifier}",
            DeviceIdentifier.FromUniqueIdentifier(identifier),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                identifier),
            SensorCategory.Actuator,
            TelemetryDataType.Boolean,
            BaseTime);

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(1));

        bool registered = await repository.TryAddAsync(sensor);

        Assert.True(registered);

        return sensor;
    }

    /// <summary>
    /// Converts a concurrent Undo attempt into an observable test result.
    /// Expected concurrency conflicts are returned rather than escaping.
    /// </summary>
    private static async Task<Exception?> AttemptUndoAsync(
        ActuatorCommandService service,
        Guid targetDeviceId,
        Guid expectedCommandId)
    {
        try
        {
            await service.UndoAsync(
                targetDeviceId,
                expectedCommandId);

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return BaseTime.AddMinutes(2);
        }
    }
}