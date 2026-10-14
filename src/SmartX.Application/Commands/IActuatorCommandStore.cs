using SmartX.Domain.Commands;

namespace SmartX.Application.Commands;

/// <summary>
/// Defines serialised, per-device command transactions.
///
/// Infrastructure owns the mutable commanded state, audit trail, and actual
/// Stack of undoable commands. Application services remain responsible for
/// validating the registered device against the authoritative repository.
/// </summary>
public interface IActuatorCommandStore
{
    /// <summary>
    /// Serialises the complete supplied operation for one target.
    ///
    /// Each device has an independent asynchronous gate. The gate covers
    /// eligibility validation and state mutation, not just dictionary access.
    /// </summary>
    Task<TResult> ExecuteSerializedAsync<TResult>(
        Guid targetDeviceId,
        Func<IActuatorCommandTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a detached state, audit, and Undo metadata snapshot.
    ///
    /// Uncommanded devices begin explicitly Off with an empty Undo Stack.
    /// </summary>
    Task<ActuatorStateSnapshot> GetSnapshotAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides operations that are valid only while the device's serialisation
/// gate is held.
///
/// Callers must not retain this transaction beyond its supplied callback.
/// Mutable store collections are never exposed.
/// </summary>
public interface IActuatorCommandTransaction
{
    /// <summary>
    /// Current simulated commanded relay state.
    /// </summary>
    RelayState CurrentState { get; }

    /// <summary>
    /// Peeks at the real per-device Stack without removing its top entry.
    ///
    /// Only successful, state-changing manual commands are pushed.
    /// Reversals and no-ops never become new undoable entries.
    /// </summary>
    CommandRecord? LatestUndoableCommand { get; }

    /// <summary>
    /// Number of successful state changes available for Undo.
    /// </summary>
    int UndoDepth { get; }

    /// <summary>
    /// Applies and audits a validated manual command.
    ///
    /// Successful state-changing commands are pushed onto the device's
    /// Stack. Accepted no-ops are audited without changing the Stack.
    /// </summary>
    void Record(CommandRecord command);

    /// <summary>
    /// Atomically records a validated reversal, restores the original
    /// previous state, and removes the expected latest Stack entry.
    ///
    /// The implementation must validate the expected Stack top, current
    /// state, target identity, and reversal linkage before mutation.
    ///
    /// If validation or reversal recording fails, state, audit, and Stack
    /// must remain unchanged.
    /// </summary>
    void Restore(
        CommandRecord expectedLatestCommand,
        CommandRecord reversal);
}