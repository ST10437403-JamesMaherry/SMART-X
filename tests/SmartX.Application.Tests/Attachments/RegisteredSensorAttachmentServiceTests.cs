using SmartX.Application.Attachments;
using SmartX.Application.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Attachments;

/// <summary>
/// Verifies that attachment operations cannot create orphan resources for
/// nonexistent Smart-X sensors.
/// </summary>
public sealed class RegisteredSensorAttachmentServiceTests
{
    private static readonly DateTimeOffset FixedTime =
        new(
            2026,
            9,
            11,
            10,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task UploadAsync_WhenSensorExists_DelegatesToAttachmentService()
    {
        var sensor =
            CreateSensor();

        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(
                sensor,
                store);

        var metadata =
            await service.UploadAsync(
                sensor.Id,
                "gateway.log",
                "text/plain",
                SensorAttachmentCategory.HardwareLog,
                new MemoryStream(
                    "gateway started"u8.ToArray()));

        Assert.Equal(
            sensor.Id,
            metadata.SensorProfileId);

        Assert.Equal(
            1,
            store.SaveCallCount);
    }

    [Fact]
    public async Task UploadAsync_WhenSensorMissing_ThrowsSensorNotFoundException()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(
                null,
                store);

        var missingId =
            Guid.NewGuid();

        var exception =
            await Assert.ThrowsAsync<
                SensorNotFoundException>(
                () => service.UploadAsync(
                    missingId,
                    "gateway.log",
                    "text/plain",
                    SensorAttachmentCategory.HardwareLog,
                    new MemoryStream(
                        "gateway started"u8.ToArray())));

        Assert.Equal(
            missingId,
            exception.SensorProfileId);

        Assert.Equal(
            0,
            store.SaveCallCount);
    }

    [Fact]
    public async Task ListAsync_WhenSensorMissing_ThrowsSensorNotFoundException()
    {
        var service =
            CreateService(
                null,
                new RecordingAttachmentStore());

        await Assert.ThrowsAsync<
            SensorNotFoundException>(
            () => service.ListAsync(
                Guid.NewGuid()));
    }

    [Fact]
    public async Task UploadAsync_WhenCancelled_DoesNotPersistAttachment()
    {
        var sensor =
            CreateSensor();

        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(
                sensor,
                store);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => service.UploadAsync(
                sensor.Id,
                "gateway.log",
                "text/plain",
                SensorAttachmentCategory.HardwareLog,
                new MemoryStream(
                    "gateway started"u8.ToArray()),
                cancellationToken:
                    cancellationSource.Token));

        Assert.Equal(
            0,
            store.SaveCallCount);
    }

    private static RegisteredSensorAttachmentService CreateService(
        SensorProfile? sensor,
        RecordingAttachmentStore store)
    {
        var attachmentService =
            new SensorAttachmentService(
                store,
                new FixedTimeProvider(
                    FixedTime));

        return new RegisteredSensorAttachmentService(
            new StubSensorRepository(
                sensor),
            attachmentService);
    }

    private static SensorProfile CreateSensor()
    {
        return SensorProfile.Create(
            "Attachment Sensor",
            DeviceIdentifier.FromMacAddress(
                "AA:BB:CC:DD:EE:A0"),
            new DeploymentLocation(
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            FixedTime);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(
            DateTimeOffset utcNow)
        {
            _utcNow =
                utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class StubSensorRepository
        : ISensorRepository
    {
        private readonly SensorProfile? _sensor;

        public StubSensorRepository(
            SensorProfile? sensor)
        {
            _sensor =
                sensor;
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SensorProfile> sensors =
                _sensor is null
                    ? Array.Empty<SensorProfile>()
                    : [_sensor];

            return Task.FromResult(
                sensors);
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? result =
                _sensor is not null &&
                _sensor.Id == sensorProfileId
                    ? _sensor
                    : null;

            return Task.FromResult(
                result);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(deviceIdentifier);

            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? sensor =
                _sensor is not null &&
                _sensor.DeviceIdentifier == deviceIdentifier
                    ? _sensor
                    : null;

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(false);
        }
    }

    private sealed class RecordingAttachmentStore
        : ISensorAttachmentStore
    {
        public int SaveCallCount
        {
            get;
            private set;
        }

        public async Task<SensorAttachmentMetadata> SaveAsync(
            SensorAttachmentWriteRequest request,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;

            using var destination =
                new MemoryStream();

            await content.CopyToAsync(
                destination,
                cancellationToken);

            return new SensorAttachmentMetadata(
                Guid.NewGuid(),
                request.SensorProfileId,
                request.FileName,
                request.ContentType,
                request.Category,
                destination.Length,
                request.UploadedAtUtc);
        }

        public Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<
                IReadOnlyList<SensorAttachmentMetadata>>(
                Array.Empty<SensorAttachmentMetadata>());
        }

        public Task<SensorAttachmentMetadata?> GetMetadataAsync(
            Guid sensorProfileId,
            Guid attachmentId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<
                SensorAttachmentMetadata?>(
                null);
        }

        public Task WriteContentAsync(
            Guid sensorProfileId,
            Guid attachmentId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}