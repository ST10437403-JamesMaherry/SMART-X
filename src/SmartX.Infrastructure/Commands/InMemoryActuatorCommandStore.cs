using System.Collections.Concurrent;

using SmartX.Application.Commands;
using SmartX.Domain.Commands;

namespace SmartX.Infrastructure.Commands;

/// <summary>
/// Process-local simulated actuator state, audit history, and genuine
/// per-device Stack-backed Undo.
///
/// Registration and device eligibility remain owned by the existing
/// sensor repository. This store owns commanded relay state only.
///
/// Independent SemaphoreSlim gates serialise complete command and Undo
/// transactions for each device without holding monitor locks across awaits.
/// </summary>
public sealed class InMemoryActuatorCommandStore : IActuatorCommandStore
{
    private readonly ConcurrentDictionary<Guid, DeviceEntry> _devices = new();

    /// <summary>
    /// Executes one complete operation under the target device's gate.
    ///
    /// Concurrent operations for the same device cannot interleave.
    /// Different devices may execute independently.
    /// </summary>
    public async Task<TResult> ExecuteSerializedAsync<TResult>(
        Guid targetDeviceId,
        Func<IActuatorCommandTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(targetDeviceId);
        ArgumentNullException.ThrowIfNull(operation);

        cancellationToken.ThrowIfCancellationRequested();

        DeviceEntry device = _devices.GetOrAdd(
            targetDeviceId,
            static id => new DeviceEntry(id));

        await device.Gate.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Mutable state is accessible only through this transaction.
            // The same gate covers both ordinary commands and Undo.
            return await operation(
                new DeviceTransaction(device),
                cancellationToken);
        }
        finally
        {
            device.Gate.Release();
        }
    }

    /// <summary>
    /// Returns a detached state, audit, and Undo metadata snapshot.
    ///
    /// The real Stack and mutable audit List are never returned.
    /// An uncommanded device has the explicit initial state Off.
    /// </summary>
    public async Task<ActuatorStateSnapshot> GetSnapshotAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(targetDeviceId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_devices.TryGetValue(targetDeviceId, out DeviceEntry? device))
        {
            return new ActuatorStateSnapshot(
                targetDeviceId,
                RelayState.Off,
                Array.AsReadOnly(Array.Empty<CommandRecord>()),
                null,
                0);
        }

        await device.Gate.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid? latestUndoableCommandId =
                device.UndoStack.TryPeek(out CommandRecord? latest)
                    ? latest.Id
                    : null;

            return new ActuatorStateSnapshot(
                targetDeviceId,
                device.CurrentState,
                Array.AsReadOnly(device.AuditTrail.ToArray()),
                latestUndoableCommandId,
                device.UndoStack.Count);
        }
        finally
        {
            device.Gate.Release();
        }
    }

    private static void ValidateTarget(Guid targetDeviceId)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid target device ID is required.",
                nameof(targetDeviceId));
        }
    }

    /// <summary>
    /// Private process-local state for exactly one device.
    ///
    /// UndoStack is the real LIFO collection required by the assessment.
    /// AuditTrail is deliberately separate so originals and reversals
    /// remain visible after Stack entries have been removed.
    /// </summary>
    private sealed class DeviceEntry
    {
        public DeviceEntry(Guid targetDeviceId)
        {
            TargetDeviceId = targetDeviceId;
        }

        public Guid TargetDeviceId { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RelayState CurrentState { get; set; } = RelayState.Off;

        public Stack<CommandRecord> UndoStack { get; } = new();

        public List<CommandRecord> AuditTrail { get; } = [];
    }

    /// <summary>
    /// Provides mutation operations while the owning DeviceEntry gate
    /// is held by ExecuteSerializedAsync.
    ///
    /// All checks occur before mutation. No asynchronous work is executed
    /// between the state, audit, and Stack updates.
    /// </summary>
    private sealed class DeviceTransaction : IActuatorCommandTransaction
    {
        private readonly DeviceEntry _device;

        public DeviceTransaction(DeviceEntry device)
        {
            _device = device;
        }

        public RelayState CurrentState => _device.CurrentState;

        public int UndoDepth => _device.UndoStack.Count;

        /// <summary>
        /// Inspects the actual Stack top without removing it.
        /// </summary>
        public CommandRecord? LatestUndoableCommand =>
            _device.UndoStack.TryPeek(out CommandRecord? latest)
                ? latest
                : null;

        /// <summary>
        /// Applies an accepted manual command.
        ///
        /// A genuine state change is pushed onto the per-device Stack.
        /// A no-op is audited but never pushed.
        ///
        /// If audit insertion fails after a successful Stack push, the
        /// pushed entry is removed before the exception escapes.
        /// State is assigned only after both operations succeed.
        /// </summary>
        public void Record(CommandRecord command)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.TargetDeviceId != _device.TargetDeviceId)
            {
                throw new ActuatorCommandConflictException(
                    "The command targets a different device.");
            }

            if (command.IsReversal)
            {
                throw new ActuatorCommandConflictException(
                    "Reversal records must use the dedicated Undo operation.");
            }

            if (command.PreviousState != _device.CurrentState)
            {
                throw new ActuatorCommandConflictException(
                    "The actuator state has changed since the command " +
                    "was prepared.");
            }

            if (command.StateChanged)
            {
                // Genuine LIFO Undo history:
                // only successful state-changing manual commands enter.
                _device.UndoStack.Push(command);

                try
                {
                    _device.AuditTrail.Add(command);
                }
                catch
                {
                    // Compensate for an unsuccessful audit insertion.
                    // The previous commanded state has not been changed.
                    _device.UndoStack.Pop();

                    throw;
                }
            }
            else
            {
                // Accepted no-ops remain auditable but are not undoable.
                _device.AuditTrail.Add(command);
            }

            _device.CurrentState = command.ResultingState;
        }

        /// <summary>
        /// Performs genuine Undo of the expected latest command.
        ///
        /// Validation:
        /// 1. Both records belong to this device.
        /// 2. The actual Stack top matches the expected original.
        /// 3. The original is a state-changing manual command.
        /// 4. Current relay state matches the original resulting state.
        /// 5. The reversal references that exact original command.
        /// 6. The reversal restores the original previous state.
        ///
        /// Commit order:
        /// 1. Append the reversal audit record.
        /// 2. Restore the commanded state.
        /// 3. Pop the original from the real Stack.
        ///
        /// The Stack is never popped before successful reversal recording
        /// and state restoration. Reversals are never pushed back onto it.
        /// </summary>
        public void Restore(
            CommandRecord expectedLatestCommand,
            CommandRecord reversal)
        {
            ArgumentNullException.ThrowIfNull(expectedLatestCommand);
            ArgumentNullException.ThrowIfNull(reversal);

            if (expectedLatestCommand.TargetDeviceId != _device.TargetDeviceId ||
                reversal.TargetDeviceId != _device.TargetDeviceId)
            {
                throw new ActuatorCommandConflictException(
                    "The Undo operation targets a different device.");
            }

            if (_device.UndoStack.Count == 0)
            {
                throw new ActuatorCommandConflictException(
                    "This device has no state-changing command to undo.");
            }

            // Use the actual Stack.Peek() result, not a caller-supplied
            // description of what might have been the latest command.
            CommandRecord actualLatest = _device.UndoStack.Peek();

            if (actualLatest.Id != expectedLatestCommand.Id ||
                actualLatest != expectedLatestCommand)
            {
                throw new ActuatorCommandConflictException(
                    "The expected command is no longer the latest " +
                    "undoable command.");
            }

            if (!actualLatest.StateChanged ||
                actualLatest.IsReversal)
            {
                throw new ActuatorCommandConflictException(
                    "Only successful manual state changes can be undone.");
            }

            if (_device.CurrentState != actualLatest.ResultingState)
            {
                throw new ActuatorCommandConflictException(
                    "The actuator state no longer matches the " +
                    "latest undoable command.");
            }

            if (!reversal.IsReversal ||
                !reversal.StateChanged ||
                reversal.ReversesCommandId != actualLatest.Id)
            {
                throw new ActuatorCommandConflictException(
                    "The reversal must reference the latest " +
                    "state-changing command.");
            }

            if (reversal.PreviousState != _device.CurrentState ||
                reversal.ResultingState != actualLatest.PreviousState)
            {
                throw new ActuatorCommandConflictException(
                    "The reversal does not restore the recorded " +
                    "previous actuator state.");
            }

            // All validation is complete.
            //
            // Audit insertion is performed before state and Stack mutation.
            // If insertion throws, both commanded state and Stack remain
            // unchanged. The original command remains undoable.
            _device.AuditTrail.Add(reversal);

            // These in-memory operations are performed under the same gate,
            // without another callback or await between them.
            _device.CurrentState = reversal.ResultingState;

            // This removes the original only after restoration succeeds.
            // The validated Stack cannot be changed concurrently.
            _device.UndoStack.Pop();

            // Do not push the reversal. Otherwise repeated Undo calls
            // could create an infinite On/Off reversal loop.
        }
    }
}