using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Maps between transport-facing sensor contracts and validated Smart-X
/// domain objects.
///
/// Keeping mapping inside the application boundary prevents API contracts
/// from leaking into the domain model and prevents domain entities from
/// being returned directly to external clients.
/// </summary>
public static class SensorContractMapper
{
    /// <summary>
    /// Converts an API registration request into a fully validated
    /// <see cref="SensorProfile"/>.
    ///
    /// Domain constructors remain responsible for their own business rules.
    /// This mapper validates transport-specific concepts such as textual
    /// enum values and identifier-kind selection.
    /// </summary>
    public static SensorProfile ToDomain(
        RegisterSensorRequest request,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.DeploymentLocation is null)
        {
            throw new SensorRegistrationValidationException(
                "A sensor deployment location is required.");
        }

        var deviceIdentifier =
            MapDeviceIdentifier(
                request.DeviceIdentifier,
                request.IdentifierKind);

        var deploymentLocation =
            new DeploymentLocation(
                request.DeploymentLocation.Facility,
                request.DeploymentLocation.Zone,
                request.DeploymentLocation.NodeId);

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
    /// Converts a validated domain sensor into the public API response model.
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

    private static DeviceIdentifier MapDeviceIdentifier(
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
                "MacAddress",
                StringComparison.OrdinalIgnoreCase))
        {
            return DeviceIdentifier.FromMacAddress(value);
        }

        if (normalisedKind.Equals(
                "UniqueIdentifier",
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
                typeof(TEnum),
                parsed))
        {
            throw new SensorRegistrationValidationException(
                $"'{normalised}' is not a supported {fieldName}.");
        }

        return parsed;
    }
}