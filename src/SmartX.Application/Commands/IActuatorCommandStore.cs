using SmartX.Domain.Commands;

namespace SmartX.Application.Commands;

/// <summary>
/// Defines serialised, per-device command transactions.
///
/// Application services perform device-authority validation inside the
/// transaction. Infrastructure controls synchronisation and stores the
/// actual resulting actuator state.
/// </summary>
public interface IActuatorCommandStore
{
    /// <summary>
    /// Serialises the complete supplied operation for one target.
    ///
    /// An asynchronous per-device gate allows repository reads to be
    /// awaited without holding a synchronous monitor lock across I/O.
    /// </summary>
    Task<TResult> ExecuteSerializedAsync<TResult>(
        Guid targetDeviceId,
        Func<IActuatorCommandTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a detached view of one simulated actuator.
    /// Uncommanded devices begin explicitly in the Off state.
    /// </summary>
    Task<ActuatorStateSnapshot> GetSnapshotAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes the state operations permitted while the device gate is held.
///
/// The mutable store and its internal collections never escape this boundary.
/// Commit 2 will extend this interface with Stack-backed reversal operations.
/// </summary>
public interface IActuatorCommandTransaction
{
    RelayState CurrentState { get; }

    /// <summary>
    /// Records an accepted manual command and applies its resulting state.
    /// A no-op is audited but does not change the relay state.
    /// </summary>
    void Record(CommandRecord command);
}