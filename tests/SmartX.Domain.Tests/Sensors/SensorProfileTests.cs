using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

using Xunit;

namespace SmartX.Domain.Tests.Sensors;

public sealed class SensorProfileTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            10,
            10,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void Create_WhenValid_CreatesExpectedSensorProfile()
    {
        var identifier =
            DeviceIdentifier.FromMacAddress(
                "AA:BB:CC:DD:EE:FF");

        var location =
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node-01");

        var sensor = SensorProfile.Create(
            "  Temperature Sensor 01  ",
            identifier,
            location,
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime);

        Assert.NotEqual(Guid.Empty, sensor.Id);
        Assert.Equal("Temperature Sensor 01", sensor.Name);
        Assert.Equal(identifier, sensor.DeviceIdentifier);
        Assert.Equal(location, sensor.DeploymentLocation);

        Assert.Equal(
            SensorCategory.Environmental,
            sensor.Category);

        Assert.Equal(
            TelemetryDataType.Float,
            sensor.DataType);

        Assert.Equal(
            SensorStatus.Offline,
            sensor.Status);

        Assert.Equal(BaseTime, sensor.CreatedAtUtc);
        Assert.Equal(BaseTime, sensor.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenNameMissing_ThrowsMeaningfulException(
        string? name)
    {
        var identifier = CreateIdentifier();
        var location = CreateLocation();

        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                name,
                identifier,
                location,
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                BaseTime));
    }

    [Fact]
    public void Create_WhenNameTooLong_ThrowsMeaningfulException()
    {
        var name =
            new string('A', SensorProfile.MaxNameLength + 1);

        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                name,
                CreateIdentifier(),
                CreateLocation(),
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                BaseTime));
    }

    [Fact]
    public void Create_WhenDeviceIdentifierMissing_ThrowsMeaningfulException()
    {
        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                "Temperature Sensor",
                null,
                CreateLocation(),
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                BaseTime));
    }

    [Fact]
    public void Create_WhenDeploymentLocationMissing_ThrowsMeaningfulException()
    {
        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                "Temperature Sensor",
                CreateIdentifier(),
                null,
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                BaseTime));
    }

    [Fact]
    public void Create_WhenCategoryUndefined_ThrowsMeaningfulException()
    {
        var invalidCategory = (SensorCategory)999;

        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                "Temperature Sensor",
                CreateIdentifier(),
                CreateLocation(),
                invalidCategory,
                TelemetryDataType.Float,
                BaseTime));
    }

    [Fact]
    public void Create_WhenTelemetryTypeUndefined_ThrowsMeaningfulException()
    {
        var invalidDataType = (TelemetryDataType)999;

        Assert.Throws<InvalidSensorProfileException>(
            () => SensorProfile.Create(
                "Temperature Sensor",
                CreateIdentifier(),
                CreateLocation(),
                SensorCategory.Environmental,
                invalidDataType,
                BaseTime));
    }

    [Fact]
    public void Create_WhenTimestampHasOffset_NormalisesTimestampToUtc()
    {
        var southAfricanTime =
            new DateTimeOffset(
                2026,
                9,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(2));

        var sensor = SensorProfile.Create(
            "Temperature Sensor",
            CreateIdentifier(),
            CreateLocation(),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            southAfricanTime);

        Assert.Equal(TimeSpan.Zero, sensor.CreatedAtUtc.Offset);

        Assert.Equal(
            new DateTimeOffset(
                2026,
                9,
                10,
                10,
                0,
                0,
                TimeSpan.Zero),
            sensor.CreatedAtUtc);
    }

    [Fact]
    public void UpdateDetails_WhenValid_UpdatesMutableProperties()
    {
        var sensor = CreateSensor();

        var newLocation =
            new DeploymentLocation(
                "Facility B",
                "Zone 4",
                "Node-17");

        var updateTime = BaseTime.AddMinutes(5);

        sensor.UpdateDetails(
            "Main Power Meter",
            newLocation,
            SensorCategory.PowerConsumption,
            TelemetryDataType.Integer,
            updateTime);

        Assert.Equal("Main Power Meter", sensor.Name);
        Assert.Equal(newLocation, sensor.DeploymentLocation);

        Assert.Equal(
            SensorCategory.PowerConsumption,
            sensor.Category);

        Assert.Equal(
            TelemetryDataType.Integer,
            sensor.DataType);

        Assert.Equal(updateTime, sensor.UpdatedAtUtc);

        // Creation time must remain immutable.
        Assert.Equal(BaseTime, sensor.CreatedAtUtc);
    }

    [Fact]
    public void UpdateDetails_WhenTimestampMovesBackwards_ThrowsException()
    {
        var sensor = CreateSensor();

        Assert.Throws<InvalidSensorProfileException>(
            () => sensor.UpdateDetails(
                "Updated Sensor",
                CreateLocation(),
                SensorCategory.Environmental,
                TelemetryDataType.Float,
                BaseTime.AddSeconds(-1)));
    }

    [Fact]
    public void ChangeStatus_WhenValid_UpdatesStatusAndTimestamp()
    {
        var sensor = CreateSensor();
        var updateTime = BaseTime.AddMinutes(1);

        sensor.ChangeStatus(
            SensorStatus.Online,
            updateTime);

        Assert.Equal(
            SensorStatus.Online,
            sensor.Status);

        Assert.Equal(
            updateTime,
            sensor.UpdatedAtUtc);
    }

    [Fact]
    public void ChangeStatus_WhenUndefinedStatusProvided_ThrowsException()
    {
        var sensor = CreateSensor();
        var invalidStatus = (SensorStatus)999;

        Assert.Throws<InvalidSensorProfileException>(
            () => sensor.ChangeStatus(
                invalidStatus,
                BaseTime.AddMinutes(1)));
    }

    [Fact]
    public void ChangeStatus_WhenTimestampMovesBackwards_ThrowsException()
    {
        var sensor = CreateSensor();

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(5));

        Assert.Throws<InvalidSensorProfileException>(
            () => sensor.ChangeStatus(
                SensorStatus.Warning,
                BaseTime.AddMinutes(4)));
    }

    [Fact]
    public void ChangeStatus_AfterDecommissioning_CannotReactivateSensor()
    {
        var sensor = CreateSensor();

        sensor.ChangeStatus(
            SensorStatus.Decommissioned,
            BaseTime.AddMinutes(1));

        var exception =
            Assert.Throws<InvalidSensorStateTransitionException>(
                () => sensor.ChangeStatus(
                    SensorStatus.Online,
                    BaseTime.AddMinutes(2)));

        Assert.Equal(
            SensorStatus.Decommissioned,
            exception.CurrentStatus);

        Assert.Equal(
            SensorStatus.Online,
            exception.RequestedStatus);

        Assert.Equal(
            SensorStatus.Decommissioned,
            sensor.Status);
    }

    private static SensorProfile CreateSensor()
    {
        return SensorProfile.Create(
            "Temperature Sensor 01",
            CreateIdentifier(),
            CreateLocation(),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime);
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:FF");
    }

    private static DeploymentLocation CreateLocation()
    {
        return new DeploymentLocation(
            "Facility A",
            "Zone 1",
            "Node-01");
    }
}