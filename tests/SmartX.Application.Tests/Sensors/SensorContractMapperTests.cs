using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Sensors;

/// <summary>
/// Verifies translation between transport-facing sensor contracts and the
/// validated Smart-X domain model.
///
/// These tests protect the architectural boundary between SmartX.Contracts
/// and SmartX.Domain while ensuring transport values are mapped safely.
/// </summary>
public sealed class SensorContractMapperTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(
            2026,
            9,
            11,
            8,
            30,
            0,
            TimeSpan.FromHours(2));

    [Fact]
    public void ToDomain_WhenMacAddressProvided_NormalisesAndMapsRequest()
    {
        var category =
            GetFirstDefinedEnumValue<SensorCategory>();

        var dataType =
            GetFirstDefinedEnumValue<TelemetryDataType>();

        var request =
            CreateValidRequest(
                category.ToString(),
                dataType.ToString());

        var sensor =
            SensorContractMapper.ToDomain(
                request,
                CreatedAt);

        Assert.Equal(
            "AA:BB:CC:DD:EE:FF",
            sensor.DeviceIdentifier.Value);

        Assert.Equal(
            "MacAddress",
            sensor.DeviceIdentifier.Kind.ToString());

        Assert.Equal(
            category,
            sensor.Category);

        Assert.Equal(
            dataType,
            sensor.DataType);

        Assert.Equal(
            CreatedAt.ToUniversalTime(),
            sensor.CreatedAtUtc);
    }

    [Fact]
    public void ToDomain_WhenUniqueIdentifierProvided_PreservesIdentifier()
    {
        var request =
            CreateValidRequest(
                GetFirstDefinedEnumValue<SensorCategory>()
                    .ToString(),
                GetFirstDefinedEnumValue<TelemetryDataType>()
                    .ToString())
            with
            {
                DeviceIdentifier = "node-alpha_01",
                IdentifierKind = "UniqueIdentifier"
            };

        var sensor =
            SensorContractMapper.ToDomain(
                request,
                CreatedAt);

        Assert.Equal(
            "node-alpha_01",
            sensor.DeviceIdentifier.Value);

        Assert.Equal(
            "UniqueIdentifier",
            sensor.DeviceIdentifier.Kind.ToString());
    }

    [Fact]
    public void ToDomain_WhenIdentifierKindMissing_ThrowsValidationException()
    {
        var request =
            CreateValidRequest(
                GetFirstDefinedEnumValue<SensorCategory>()
                    .ToString(),
                GetFirstDefinedEnumValue<TelemetryDataType>()
                    .ToString())
            with
            {
                IdentifierKind = null
            };

        var exception =
            Assert.Throws<
                SensorRegistrationValidationException>(
                () => SensorContractMapper.ToDomain(
                    request,
                    CreatedAt));

        Assert.Contains(
            "identifier kind",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToDomain_WhenIdentifierKindUnsupported_ThrowsValidationException()
    {
        var request =
            CreateValidRequest(
                GetFirstDefinedEnumValue<SensorCategory>()
                    .ToString(),
                GetFirstDefinedEnumValue<TelemetryDataType>()
                    .ToString())
            with
            {
                IdentifierKind = "SerialNumber"
            };

        var exception =
            Assert.Throws<
                SensorRegistrationValidationException>(
                () => SensorContractMapper.ToDomain(
                    request,
                    CreatedAt));

        Assert.Contains(
            "not a supported device identifier",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToDomain_WhenCategoryUnsupported_ThrowsValidationException()
    {
        var request =
            CreateValidRequest(
                "NotARealCategory",
                GetFirstDefinedEnumValue<TelemetryDataType>()
                    .ToString());

        var exception =
            Assert.Throws<
                SensorRegistrationValidationException>(
                () => SensorContractMapper.ToDomain(
                    request,
                    CreatedAt));

        Assert.Contains(
            "sensor category",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToDomain_WhenTelemetryDataTypeUnsupported_ThrowsValidationException()
    {
        var request =
            CreateValidRequest(
                GetFirstDefinedEnumValue<SensorCategory>()
                    .ToString(),
                "ComplexNumber");

        var exception =
            Assert.Throws<
                SensorRegistrationValidationException>(
                () => SensorContractMapper.ToDomain(
                    request,
                    CreatedAt));

        Assert.Contains(
            "telemetry data type",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToDomain_WhenDeploymentLocationMissing_ThrowsValidationException()
    {
        var request =
            CreateValidRequest(
                GetFirstDefinedEnumValue<SensorCategory>()
                    .ToString(),
                GetFirstDefinedEnumValue<TelemetryDataType>()
                    .ToString())
            with
            {
                DeploymentLocation = null
            };

        var exception =
            Assert.Throws<
                SensorRegistrationValidationException>(
                () => SensorContractMapper.ToDomain(
                    request,
                    CreatedAt));

        Assert.Contains(
            "deployment location",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToResponse_WhenSensorProvided_MapsAllProperties()
    {
        var category =
            GetFirstDefinedEnumValue<SensorCategory>();

        var dataType =
            GetFirstDefinedEnumValue<TelemetryDataType>();

        var sensor =
            SensorProfile.Create(
                "Greenhouse Sensor",
                DeviceIdentifier.FromMacAddress(
                    "AA-BB-CC-DD-EE-FF"),
                new DeploymentLocation(
                    "Facility A",
                    "Zone 1",
                    "Node 14"),
                category,
                dataType,
                CreatedAt);

        var response =
            SensorContractMapper.ToResponse(sensor);

        Assert.Equal(
            sensor.Id,
            response.Id);

        Assert.Equal(
            sensor.Name,
            response.Name);

        Assert.Equal(
            sensor.DeviceIdentifier.Value,
            response.DeviceIdentifier);

        Assert.Equal(
            sensor.DeviceIdentifier.Kind.ToString(),
            response.IdentifierKind);

        Assert.Equal(
            sensor.DeploymentLocation.Facility,
            response.DeploymentLocation.Facility);

        Assert.Equal(
            sensor.DeploymentLocation.Zone,
            response.DeploymentLocation.Zone);

        Assert.Equal(
            sensor.DeploymentLocation.NodeId,
            response.DeploymentLocation.NodeId);

        Assert.Equal(
            sensor.Category.ToString(),
            response.Category);

        Assert.Equal(
            sensor.Status.ToString(),
            response.Status);

        Assert.Equal(
            sensor.DataType.ToString(),
            response.TelemetryDataType);

        Assert.Equal(
            sensor.CreatedAtUtc,
            response.CreatedAtUtc);

        Assert.Equal(
            sensor.UpdatedAtUtc,
            response.UpdatedAtUtc);
    }

    [Fact]
    public void ApplyUpdate_WhenValid_UpdatesMutableDomainProperties()
    {
        var sensor =
            SensorProfile.Create(
                "Original Sensor",
                DeviceIdentifier.FromMacAddress(
                    "AA:BB:CC:DD:EE:10"),
                new DeploymentLocation(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                CreatedAt);

        var request =
            new UpdateSensorRequest(
                "Updated Sensor",
                new SensorDeploymentLocationRequest(
                    "Facility B",
                    "Zone 2",
                    "Node 9"),
                SensorCategory.PowerConsumption.ToString(),
                TelemetryDataType.Integer.ToString());

        var updateTime =
            CreatedAt.AddHours(1);

        SensorContractMapper.ApplyUpdate(
            sensor,
            request,
            updateTime);

        Assert.Equal(
            "Updated Sensor",
            sensor.Name);

        Assert.Equal(
            "Facility B",
            sensor.DeploymentLocation.Facility);

        Assert.Equal(
            "Zone 2",
            sensor.DeploymentLocation.Zone);

        Assert.Equal(
            "Node 9",
            sensor.DeploymentLocation.NodeId);

        Assert.Equal(
            SensorCategory.PowerConsumption,
            sensor.Category);

        Assert.Equal(
            TelemetryDataType.Integer,
            sensor.DataType);

        Assert.Equal(
            updateTime.ToUniversalTime(),
            sensor.UpdatedAtUtc);

        // Physical device identity must remain immutable during profile
        // updates.
        Assert.Equal(
            "AA:BB:CC:DD:EE:10",
            sensor.DeviceIdentifier.Value);
    }

    [Fact]
    public void ApplyUpdate_WhenInvalidCategory_DoesNotMutateSensor()
    {
        var sensor =
            SensorProfile.Create(
                "Original Sensor",
                DeviceIdentifier.FromMacAddress(
                    "AA:BB:CC:DD:EE:11"),
                new DeploymentLocation(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                CreatedAt);

        var request =
            new UpdateSensorRequest(
                "Should Not Be Applied",
                new SensorDeploymentLocationRequest(
                    "Facility B",
                    "Zone 2",
                    "Node 2"),
                "InvalidCategory",
                TelemetryDataType.Integer.ToString());

        Assert.Throws<
            SensorRegistrationValidationException>(
            () => SensorContractMapper.ApplyUpdate(
                sensor,
                request,
                CreatedAt.AddHours(1)));

        // Mapping and validation must complete before SensorProfile is
        // mutated, preventing partially applied updates.
        Assert.Equal(
            "Original Sensor",
            sensor.Name);

        Assert.Equal(
            "Facility A",
            sensor.DeploymentLocation.Facility);

        Assert.Equal(
            SensorCategory.Environmental,
            sensor.Category);

        Assert.Equal(
            TelemetryDataType.Float,
            sensor.DataType);
    }

    private static RegisterSensorRequest CreateValidRequest(
        string category,
        string dataType)
    {
        return new RegisterSensorRequest(
            "Greenhouse Sensor",
            "aa-bb-cc-dd-ee-ff",
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Facility A",
                "Zone 1",
                "Node 14"),
            category,
            dataType);
    }

    private static TEnum GetFirstDefinedEnumValue<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()[0];
    }
}