using System.Collections.Concurrent;

using SmartX.Application.Commands;
using SmartX.Domain.Commands;

namespace SmartX.Infrastructure.Commands;

/// <summary>
/// Process-local commanded actuator state with independent per-device gates.
///
/// The sensor repository remains the registration authority. This store owns
/// only simulated relay state and command audit records, not device metadata,
/// telemetry readings, or operational health.
/// </summary>
public sealed class InMemoryActuatorCommandStore : IActuatorCommandStore
{
    private readonly ConcurrentDictionary<Guid, DeviceEntry> _devices = new();

    /// <summary>
    /// Serialises each target's entire command operation using SemaphoreSlim.
    ///
    /// Commands for different targets are independent. The gate also covers
    /// awaited authority validation, preventing another command for the same
    /// target from intervening between validation and application.
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

            // Only the holder of this device's gate may access the mutable
            // transaction. No mutable collection is returned to the caller.
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
    /// Returns a detached state and audit snapshot under the same gate.
    ///
    /// An uncommanded device is explicitly Off. Read operations do not need
    /// to materialise an entry in the command dictionary.
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
                Array.AsReadOnly(Array.Empty<CommandRecord>()));
        }

        await device.Gate.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            return new ActuatorStateSnapshot(
                targetDeviceId,
                device.CurrentState,
                Array.AsReadOnly(device.AuditTrail.ToArray()));
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
    /// One process-local device's private state.
    ///
    /// The command audit is deliberately separate from the Undo stack that
    /// will be introduced in Commit 2. Audit records must survive reversals.
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

        public List<CommandRecord> AuditTrail { get; } = [];
    }

    /// <summary>
    /// Restricts mutation to a holder of the target's serialisation gate.
    ///
    /// The current state and target identity are checked again at recording
    /// time so stale or mismatched command records cannot corrupt the store.
    /// </summary>
    private sealed class DeviceTransaction : IActuatorCommandTransaction
    {
        private readonly DeviceEntry _device;

        public DeviceTransaction(DeviceEntry device)
        {
            _device = device;
        }

        public RelayState CurrentState => _device.CurrentState;

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

            // Append before assigning the new state. If audit insertion
            // fails, the commanded state remains unchanged.
            _device.AuditTrail.Add(command);

            _device.CurrentState = command.ResultingState;
        }
    }
}