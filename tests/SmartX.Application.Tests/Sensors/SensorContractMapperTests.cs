using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Sensors;

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