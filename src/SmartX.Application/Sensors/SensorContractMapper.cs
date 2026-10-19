using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Maps between transport-facing sensor contracts and validated Smart-X
/// domain objects.
///
/// Keeping mapping inside the Application boundary prevents transport models
/// from leaking into the Domain and prevents mutable Domain entities from
/// being returned directly to external clients.
/// </summary>
public static class SensorContractMapper
{
    /// <summary>
    /// Converts an API registration request into a validated sensor profile.
    /// </summary>
    public static SensorProfile ToDomain(
        RegisterSensorRequest request,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deploymentLocation =
            MapDeploymentLocation(
                request.DeploymentLocation);

        var deviceIdentifier =
            MapDeviceIdentifier(
                request.DeviceIdentifier,
                request.IdentifierKind);

        var category =
            ParseDefinedEnum<SensorCategory>(
                request.Category,
                "sensor category");

        var telemetryDataType =
            ParseDefinedEnum<TelemetryDataType>(
                request.TelemetryDataType,
                "telemetry data type");

        return SensorProfile.Create(
            request.Name,
            deviceIdentifier,
            deploymentLocation,
            category,
            telemetryDataType,
            createdAtUtc);
    }

    /// <summary>
    /// Applies an API update request to an existing sensor profile.
    ///
    /// All transport values are converted before the Domain entity is
    /// mutated. SensorProfile.UpdateDetails then performs the final Domain
    /// validation and guarantees that failed updates do not partially modify
    /// the entity.
    /// </summary>
    public static void ApplyUpdate(
        SensorProfile sensor,
        UpdateSensorRequest request,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(request);

        var deploymentLocation =
            MapDeploymentLocation(
                request.DeploymentLocation);

        var category =
            ParseDefinedEnum<SensorCategory>(
                request.Category,
                "sensor category");

        var telemetryDataType =
            ParseDefinedEnum<TelemetryDataType>(
                request.TelemetryDataType,
                "telemetry data type");

        sensor.UpdateDetails(
            request.Name,
            deploymentLocation,
            category,
            telemetryDataType,
            updatedAtUtc);
    }

    /// <summary>
    /// Converts a validated Domain sensor into its public API response model.
    /// </summary>
    public static SensorResponse ToResponse(
        SensorProfile sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return new SensorResponse(
            sensor.Id,
            sensor.Name,
            sensor.DeviceIdentifier.Value,
            sensor.DeviceIdentifier.Kind.ToString(),
            new SensorDeploymentLocationResponse(
                sensor.DeploymentLocation.Facility,
                sensor.DeploymentLocation.Zone,
                sensor.DeploymentLocation.NodeId),
            sensor.Category.ToString(),
            sensor.Status.ToString(),
            sensor.DataType.ToString(),
            sensor.CreatedAtUtc,
            sensor.UpdatedAtUtc);
    }

    private static DeploymentLocation MapDeploymentLocation(
        SensorDeploymentLocationRequest? request)
    {
        if (request is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor deployment location is required.");
        }

        return new DeploymentLocation(
            request.Facility,
            request.Zone,
            request.NodeId);
    }

    /// <summary>
    /// Reuses the same physical-identifier parsing for registration and exact
    /// lookup. MAC normalisation and custom-ID case rules belong to Domain.
    /// </summary>
    public static DeviceIdentifier MapDeviceIdentifier(
        string? value,
        string? identifierKind)
    {
        if (string.IsNullOrWhiteSpace(identifierKind))
        {
            throw new SensorRegistrationValidationException(
                "A device identifier kind is required.");
        }

        var normalisedKind =
            identifierKind.Trim();

        if (normalisedKind.Equals(
                nameof(DeviceIdentifierKind.MacAddress),
                StringComparison.OrdinalIgnoreCase))
        {
            return DeviceIdentifier.FromMacAddress(value);
        }

        if (normalisedKind.Equals(
                nameof(DeviceIdentifierKind.UniqueIdentifier),
                StringComparison.OrdinalIgnoreCase))
        {
            return DeviceIdentifier.FromUniqueIdentifier(value);
        }

        throw new SensorRegistrationValidationException(
            $"'{normalisedKind}' is not a supported device identifier " +
            "kind. Use 'MacAddress' or 'UniqueIdentifier'.");
    }

    private static TEnum ParseDefinedEnum<TEnum>(
        string? value,
        string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SensorRegistrationValidationException(
                $"A {fieldName} is required.");
        }

        var normalised =
            value.Trim();

        if (!Enum.TryParse<TEnum>(
                normalised,
                ignoreCase: true,
                out var parsed) ||
            !Enum.IsDefined(
                parsed))
        {
            throw new SensorRegistrationValidationException(
                $"'{normalised}' is not a supported {fieldName}.");
        }

        return parsed;
    }
}