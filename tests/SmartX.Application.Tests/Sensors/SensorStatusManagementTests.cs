using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Sensors;

/// <summary>
/// Verifies the Part 1 sensor-state operations used by the Adaptive Anomaly
/// Spotlight.
///
/// These tests focus on application orchestration. SensorProfile itself
/// remains responsible for enforcing legal Domain state transitions.
/// </summary>
public sealed class SensorStatusManagementTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            14,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task ChangeStatusAsync_WhenTransitionValid_UpdatesSensor()
    {
        SensorProfile sensor =
            CreateSensor();

        var repository =
            new RecordingSensorRepository(
                sensor);

        var service =
            CreateService(
                repository);

        SensorStatusChangeResult result =
            await service.ChangeStatusAsync(
                sensor.Id,
                new ChangeSensorStatusRequest(
                    "Online"));

        Assert.True(
            result.Changed);

        Assert.Equal(
            SensorStatus.Offline,
            result.PreviousStatus);

        Assert.Equal(
            SensorStatus.Online,
            result.CurrentStatus);

        Assert.Equal(
            "Online",
            result.Sensor.Status);

        Assert.Equal(
            1,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task ChangeStatusAsync_WhenStateUnchanged_IsIdempotent()
    {
        SensorProfile sensor =
            CreateSensor();

        var repository =
            new RecordingSensorRepository(
                sensor);

        var service =
            CreateService(
                repository);

        SensorStatusChangeResult result =
            await service.ChangeStatusAsync(
                sensor.Id,
                new ChangeSensorStatusRequest(
                    "Offline"));

        Assert.False(
            result.Changed);

        Assert.Equal(
            SensorStatus.Offline,
            result.PreviousStatus);

        Assert.Equal(
            SensorStatus.Offline,
            result.CurrentStatus);

        /*
         * An unchanged state must not perform a redundant persistence update
         * or create a false dashboard state-change occurrence.
         */
        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task MarkOnlineFromTelemetryAsync_WhenOffline_MarksSensorOnline()
    {
        SensorProfile sensor =
            CreateSensor();

        var repository =
            new RecordingSensorRepository(
                sensor);

        var service =
            CreateService(
                repository);

        SensorStatusChangeResult? result =
            await service.MarkOnlineFromTelemetryAsync(
                sensor.Id);

        Assert.NotNull(
            result);

        Assert.True(
            result.Changed);

        Assert.Equal(
            SensorStatus.Offline,
            result.PreviousStatus);

        Assert.Equal(
            SensorStatus.Online,
            result.CurrentStatus);

        Assert.Equal(
            SensorStatus.Online,
            sensor.Status);

        Assert.Equal(
            1,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task MarkOnlineFromTelemetryAsync_WhenAlreadyOnline_DoesNothing()
    {
        SensorProfile sensor =
            CreateSensor();

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(-1));

        var repository =
            new RecordingSensorRepository(
                sensor);

        var service =
            CreateService(
                repository);

        SensorStatusChangeResult? result =
            await service.MarkOnlineFromTelemetryAsync(
                sensor.Id);

        Assert.Null(
            result);

        Assert.Equal(
            SensorStatus.Online,
            sensor.Status);

        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    [Fact]
    public async Task ChangeStatusAsync_WhenStatusUnsupported_RejectsRequest()
    {
        SensorProfile sensor =
            CreateSensor();

        var repository =
            new RecordingSensorRepository(
                sensor);

        var service =
            CreateService(
                repository);

        var exception =
            await Assert.ThrowsAsync<
                SensorRegistrationValidationException>(
                () =>
                    service.ChangeStatusAsync(
                        sensor.Id,
                        new ChangeSensorStatusRequest(
                            "Exploding")));

        Assert.Contains(
            "supported sensor status",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            repository.UpdateCallCount);
    }

    private static SensorManagementService CreateService(
        RecordingSensorRepository repository)
    {
        return new SensorManagementService(
            repository,
            new FixedTimeProvider(
                BaseTime));
    }

    private static SensorProfile CreateSensor()
    {
        return SensorProfile.Create(
            "State Test Sensor",
            DeviceIdentifier.FromMacAddress(
                "AA:BB:CC:DD:EE:D1"),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            BaseTime.AddHours(-1));
    }

    /// <summary>
    /// In-memory repository double that records persistence calls while
    /// retaining the real SensorProfile Domain object.
    /// </summary>
    private sealed class RecordingSensorRepository
        : ISensorRepository
    {
        private readonly Dictionary<Guid, SensorProfile>
            _sensors;

        public RecordingSensorRepository(
            SensorProfile sensor)
        {
            _sensors =
                new Dictionary<Guid, SensorProfile>
                {
                    [sensor.Id] =
                        sensor
                };
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
                return Task.FromResult(
                    false);
            }

            _sensors.Add(
                sensor.Id,
                sensor);

            return Task.FromResult(
                true);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SensorProfile> result =
                _sensors
                    .Values
                    .ToArray();

            return Task.FromResult(
                result);
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _sensors.TryGetValue(
                sensorProfileId,
                out SensorProfile? sensor);

            return Task.FromResult(
                sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sensors.ContainsKey(
                    sensor.Id))
            {
                return Task.FromResult(
                    false);
            }

            _sensors[sensor.Id] =
                sensor;

            UpdateCallCount++;

            return Task.FromResult(
                true);
        }
    }

    /// <summary>
    /// Deterministic clock used to verify state transitions without depending
    /// on the machine running the test.
    /// </summary>
    private sealed class FixedTimeProvider
        : TimeProvider
    {
        private readonly DateTimeOffset _currentTime;

        public FixedTimeProvider(
            DateTimeOffset currentTime)
        {
            _currentTime =
                currentTime;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _currentTime;
        }
    }
}