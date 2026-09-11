using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Defines persistence operations required by sensor registration.
///
/// Concrete storage technology is deliberately hidden from the application
/// layer. A later implementation may use an in-memory dictionary, relational
/// database or another persistence mechanism without changing registration
/// business logic.
/// </summary>
public interface ISensorRepository
{
    /// <summary>
    /// Attempts to add a sensor profile atomically.
    ///
    /// Returns false when another sensor with the same normalised device
    /// identifier already exists.
    ///
    /// The atomic contract avoids a check-then-insert race where concurrent
    /// registrations could both observe that an identifier was available
    /// before attempting to persist it.
    /// </summary>
    Task<bool> TryAddAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default);
}