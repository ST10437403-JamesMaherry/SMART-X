using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Sensors;

public sealed class SensorManagementServiceTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(
            2026,
            9,
            11,
            8,
            0,
            0,
            TimeSpan.Zero);

    private static readonly DateTimeOffset UpdatedAtUtc =
        CreatedAtUtc.AddHours(1);

    [Fact]
    public async Task GetAllAsync_WhenSensorsExist_ReturnsMappedResponses()
    {
        var first =
            CreateSensor(
                "Environmental Sensor",
                "AA:BB:CC:DD:EE:01");

        var second =
            CreateSensor(
                "Power Sensor",
                "AA:BB:CC:DD:EE:02");

        var repository =
            new StubSensorRepository(
                [first, second]);

        var service =
            CreateService(repository);

        var result =
            await service.GetAllAsync();

        Assert.Equal(2, result.Count);

        Assert.Equal(
            first.Id,
            result[0].Id);

        Assert.Equal(
            second.Id,
            result[1].Id);
    }

    [Fact]
    public async Task GetAllAsync_WhenRepositoryEmpty_ReturnsEmptyCollection()
    {
        var repository =
            new StubSensorRepository([]);

        var service =
            CreateService(repository);

        var result =
            await service.GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdAsync_WhenSensorExists_ReturnsMappedSensor()
    {
        var sensor =
            CreateSensor(
                "Environmental Sensor",
                "AA:BB:CC:DD:EE:03");

        var repository =
            new StubSensorRepository([sensor]);

        var service =
            CreateService(repository);

        var result =
            await service.GetByIdAsync(
                sensor.Id);

        Assert.Equal(
            sensor.Id,
            result.Id);

        Assert.Equal(
            sensor.DeviceIdentifier.Value,
            result.DeviceIdentifier);
    }

    [Fact]
    public async Task GetByIdAsync_WhenSensorMissing_ThrowsNotFoundException()
    {
        var repository =
            new StubSensorRepository([]);

        var service =
            CreateService(repository);

        var missingId =
            Guid.NewGuid();

        var exception =
            await Assert.ThrowsAsync<
                SensorNotFoundException>(
                () => service.GetByIdAsync(
                    missingId));

        Assert.Equal(
            missingId,
            exception.SensorProfileId);
    }

    [Fact]
    public async Task UpdateAsync_WhenValid_UpdatesMutableSensorDetails()
    {
        var sensor =
            CreateSensor(
                "Original Sensor",
                "AA:BB:CC:DD:EE:04");

        var repository =
            new StubSensorRepository([sensor]);

        var service =
            CreateService(repository);

        var request =
            new UpdateSensorRequest(
                "Updated Sensor",
                new SensorDeploymentLocationRequest(
                    "Facility B",
                    "Zone 9",
                    "Node 42"),
                SensorCategory.PowerConsumption.ToString(),
                TelemetryDataType.Integer.ToString());

        var result =
            await service.UpdateAsync(
                sensor.Id,
                request);

        Assert.Equal(
            "Updated Sensor",
            result.Name);

        Assert.Equal(
            "Facility B",
            result.DeploymentLocation.Facility);

        Assert.Equal(
            "Zone 9",
            result.DeploymentLocation.Zone);

        Assert.Equal(
            "Node 42",
            result.DeploymentLocation.NodeId);

        Assert.Equal(
            SensorCategory.PowerConsumption.ToString(),
            result.Category);

        Assert.Equal(
            TelemetryDataType.Integer.ToString(),
            result.TelemetryDataType);

        Assert.Equal(
            UpdatedAtUtc,
            result.UpdatedAtUtc);

        // Physical device identity must remain immutable.
        Assert.Equal(
            "AA:BB:CC:DD:EE:04",
            result.DeviceIdentifier);

        Assert.Equal(
            1,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateAsync_WhenSensorMissing_DoesNotAttemptUpdate()
    {
        var repository =
            new StubSensorRepository([]);

        var service =
            CreateService(repository);

        var request =
            CreateUpdateRequest();

        await Assert.ThrowsAsync<
            SensorNotFoundException>(
            () => service.UpdateAsync(
                Guid.NewGuid(),
                request));

        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateAsync_WhenCategoryInvalid_DoesNotPersistMutation()
    {
        var sensor =
            CreateSensor(
                "Original Sensor",
                "AA:BB:CC:DD:EE:05");

        var repository =
            new StubSensorRepository([sensor]);

        var service =
            CreateService(repository);

        var request =
            CreateUpdateRequest() with
            {
                Category = "UnsupportedCategory"
            };

        await Assert.ThrowsAsync<
            SensorRegistrationValidationException>(
            () => service.UpdateAsync(
                sensor.Id,
                request));

        Assert.Equal(
            "Original Sensor",
            sensor.Name);

        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateAsync_WhenCancelled_DoesNotPersistUpdate()
    {
        var sensor =
            CreateSensor(
                "Original Sensor",
                "AA:BB:CC:DD:EE:06");

        var repository =
            new StubSensorRepository([sensor]);

        var service =
            CreateService(repository);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => service.UpdateAsync(
                sensor.Id,
                CreateUpdateRequest(),
                cancellationSource.Token));

        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    private static SensorManagementService CreateService(
        StubSensorRepository repository)
    {
        return new SensorManagementService(
            repository,
            new FixedTimeProvider(
                UpdatedAtUtc));
    }

    private static SensorProfile CreateSensor(
        string name,
        string macAddress)
    {
        return SensorProfile.Create(
            name,
            DeviceIdentifier.FromMacAddress(
                macAddress),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            CreatedAtUtc);
    }

    private static UpdateSensorRequest CreateUpdateRequest()
    {
        return new UpdateSensorRequest(
            "Updated Sensor",
            new SensorDeploymentLocationRequest(
                "Facility B",
                "Zone 2",
                "Node 2"),
            SensorCategory.Environmental.ToString(),
            TelemetryDataType.Float.ToString());
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(
            DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class StubSensorRepository
        : ISensorRepository
    {
        private readonly Dictionary<Guid, SensorProfile> _sensors;

        public StubSensorRepository(
            IEnumerable<SensorProfile> sensors)
        {
            _sensors =
                sensors.ToDictionary(
                    sensor => sensor.Id);
        }

        public int UpdateCallCount
        {
            get;
            private set;
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_sensors.Values.Any(
                    existing =>
                        existing.DeviceIdentifier ==
                        sensor.DeviceIdentifier))
            {
                return Task.FromResult(false);
            }

            _sensors.Add(
                sensor.Id,
                sensor);

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<SensorProfile>>(
                _sensors.Values.ToArray());
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _sensors.TryGetValue(
                sensorProfileId,
                out var sensor);

            return Task.FromResult(sensor);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(deviceIdentifier);

            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? sensor =
                _sensors.Values.FirstOrDefault(
                    existing => existing.DeviceIdentifier == deviceIdentifier);

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UpdateCallCount++;

            if (!_sensors.ContainsKey(sensor.Id))
            {
                return Task.FromResult(false);
            }

            _sensors[sensor.Id] =
                sensor;

            return Task.FromResult(true);
        }
    }
}