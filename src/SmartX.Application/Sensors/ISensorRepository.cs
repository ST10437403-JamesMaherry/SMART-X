using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Defines persistence operations required by Smart-X sensor application
/// services.
///
/// The Application layer depends only on this abstraction. Infrastructure
/// is therefore free to use in-memory storage, a relational database or
/// another persistence technology without changing the business workflow.
/// </summary>
public interface ISensorRepository
{
    /// <summary>
    /// Attempts to add a sensor profile atomically.
    ///
    /// Returns false when another sensor already uses the same normalised
    /// device identifier.
    /// </summary>
    Task<bool> TryAddAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all currently registered sensor profiles.
    /// </summary>
    Task<IReadOnlyList<SensorProfile>> GetAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a sensor profile by its internal Smart-X identifier.
    ///
    /// Returns null when the sensor does not exist.
    /// </summary>
    Task<SensorProfile?> GetByIdAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a profile by its validated physical-device identifier.
    ///
    /// Equivalent MAC formats share one canonical lookup. Identifier kind
    /// remains significant and custom identifiers retain ordinal case rules.
    /// Returns null when the device has not been registered.
    /// </summary>
    Task<SensorProfile?> GetByDeviceIdentifierAsync(
        DeviceIdentifier deviceIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes to an existing sensor profile.
    ///
    /// Returns false when the sensor no longer exists.
    /// Device identifiers remain immutable.
    /// </summary>
    Task<bool> UpdateAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken = default);
}