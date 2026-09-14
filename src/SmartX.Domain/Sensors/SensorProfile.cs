using SmartX.Domain.Exceptions;

namespace SmartX.Domain.Sensors;

/// <summary>
/// Core Smart-X domain entity representing a registered IoT sensor.
///
/// The profile owns the rules that determine whether sensor metadata and
/// state changes are valid. This prevents invalid sensor objects from being
/// propagated into application, API, persistence or telemetry layers.
/// </summary>
public sealed class SensorProfile
{
    public const int MaxNameLength = 100;

    private SensorProfile(
        Guid id,
        string name,
        DeviceIdentifier deviceIdentifier,
        DeploymentLocation deploymentLocation,
        SensorCategory category,
        TelemetryDataType dataType,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        DeviceIdentifier = deviceIdentifier;
        DeploymentLocation = deploymentLocation;
        Category = category;
        DataType = dataType;

        // A newly registered profile is not considered online until
        // the gateway has evidence that the physical device is active.
        Status = SensorStatus.Offline;

        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Internal Smart-X identity of the sensor profile.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Human-readable sensor profile name.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Immutable physical-device identifier.
    /// </summary>
    public DeviceIdentifier DeviceIdentifier { get; }

    /// <summary>
    /// Current physical deployment location.
    /// </summary>
    public DeploymentLocation DeploymentLocation { get; private set; }

    /// <summary>
    /// Functional sensor category.
    /// </summary>
    public SensorCategory Category { get; private set; }

    /// <summary>
    /// Expected data type produced by this sensor.
    /// </summary>
    public TelemetryDataType DataType { get; private set; }

    /// <summary>
    /// Current operational state of the device.
    /// </summary>
    public SensorStatus Status { get; private set; }

    /// <summary>
    /// UTC timestamp recording when the profile was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// UTC timestamp recording the most recent domain update.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a new validated sensor profile.
    ///
    /// The calling application layer supplies the current time so that
    /// time can later come from the injected TimeProvider rather than
    /// being hidden inside the domain entity.
    /// </summary>
    public static SensorProfile Create(
        string? name,
        DeviceIdentifier? deviceIdentifier,
        DeploymentLocation? deploymentLocation,
        SensorCategory category,
        TelemetryDataType dataType,
        DateTimeOffset createdAtUtc)
    {
        var validatedName = ValidateName(name);

        if (deviceIdentifier is null)
        {
            throw new InvalidSensorProfileException(
                "A sensor device identifier is required.");
        }

        if (deploymentLocation is null)
        {
            throw new InvalidSensorProfileException(
                "A sensor deployment location is required.");
        }

        ValidateCategory(category);
        ValidateTelemetryDataType(dataType);

        var normalisedCreatedAt = createdAtUtc.ToUniversalTime();

        return new SensorProfile(
            Guid.NewGuid(),
            validatedName,
            deviceIdentifier,
            deploymentLocation,
            category,
            dataType,
            normalisedCreatedAt);
    }

    /// <summary>
    /// Updates mutable sensor profile information while preserving
    /// the immutable sensor and device identities.
    /// </summary>
    public void UpdateDetails(
        string? name,
        DeploymentLocation? deploymentLocation,
        SensorCategory category,
        TelemetryDataType dataType,
        DateTimeOffset updatedAtUtc)
    {
        var validatedName = ValidateName(name);

        if (deploymentLocation is null)
        {
            throw new InvalidSensorProfileException(
                "A sensor deployment location is required.");
        }

        ValidateCategory(category);
        ValidateTelemetryDataType(dataType);

        var normalisedUpdatedAt = ValidateUpdateTimestamp(updatedAtUtc);

        // All validation is completed before mutation so that a failed
        // update cannot leave the entity in a partially modified state.
        Name = validatedName;
        DeploymentLocation = deploymentLocation;
        Category = category;
        DataType = dataType;
        UpdatedAtUtc = normalisedUpdatedAt;
    }

    /// <summary>
    /// Changes the current operational state of the sensor.
    ///
    /// Decommissioned is intentionally treated as a terminal state.
    /// A retired physical device must be registered as a new device
    /// instead of silently reactivating its previous profile.
    /// </summary>
    public void ChangeStatus(
        SensorStatus newStatus,
        DateTimeOffset updatedAtUtc)
    {
        ValidateStatus(newStatus);

        if (Status == SensorStatus.Decommissioned &&
            newStatus != SensorStatus.Decommissioned)
        {
            throw new InvalidSensorStateTransitionException(
                Status,
                newStatus);
        }

        var normalisedUpdatedAt = ValidateUpdateTimestamp(updatedAtUtc);

        Status = newStatus;
        UpdatedAtUtc = normalisedUpdatedAt;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidSensorProfileException(
                "A sensor profile name is required.");
        }

        var normalised = name.Trim();

        if (normalised.Length > MaxNameLength)
        {
            throw new InvalidSensorProfileException(
                $"A sensor profile name cannot exceed " +
                $"{MaxNameLength} characters.");
        }

        if (normalised.Any(char.IsControl))
        {
            throw new InvalidSensorProfileException(
                "A sensor profile name contains invalid control characters.");
        }

        return normalised;
    }

    private static void ValidateCategory(SensorCategory category)
    {
        if (!Enum.IsDefined(typeof(SensorCategory), category))
        {
            throw new InvalidSensorProfileException(
                $"'{category}' is not a supported sensor category.");
        }
    }

    private static void ValidateTelemetryDataType(
        TelemetryDataType dataType)
    {
        if (!Enum.IsDefined(typeof(TelemetryDataType), dataType))
        {
            throw new InvalidSensorProfileException(
                $"'{dataType}' is not a supported telemetry data type.");
        }
    }

    private static void ValidateStatus(SensorStatus status)
    {
        if (!Enum.IsDefined(typeof(SensorStatus), status))
        {
            throw new InvalidSensorProfileException(
                $"'{status}' is not a supported sensor status.");
        }
    }

    private DateTimeOffset ValidateUpdateTimestamp(
        DateTimeOffset updatedAtUtc)
    {
        var normalised = updatedAtUtc.ToUniversalTime();

        if (normalised < UpdatedAtUtc)
        {
            throw new InvalidSensorProfileException(
                "A sensor update timestamp cannot be earlier than its " +
                "current update timestamp.");
        }

        return normalised;
    }
}