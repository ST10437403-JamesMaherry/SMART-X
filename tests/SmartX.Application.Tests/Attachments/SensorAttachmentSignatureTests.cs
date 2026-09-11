using SmartX.Application.Attachments;

namespace SmartX.Application.Tests.Attachments;

/// <summary>
/// Verifies that binary attachment content is inspected without losing the
/// bytes consumed during signature validation.
/// </summary>
public sealed class SensorAttachmentSignatureTests
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
    public async Task UploadAsync_WithValidPngSignature_PreservesCompleteContent()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        byte[] payload =
        [
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A,
            0x01,
            0x02,
            0x03,
            0x04
        ];

        await service.UploadAsync(
            Guid.NewGuid(),
            "deployment.png",
            "image/png",
            SensorAttachmentCategory.DeploymentPhoto,
            new MemoryStream(payload));

        Assert.Equal(
            1,
            store.SaveCallCount);

        Assert.Equal(
            payload,
            store.StoredContent);
    }

    [Fact]
    public async Task UploadAsync_WithFakePngSignature_RejectsBeforePersistence()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        var payload =
            "This is not really a PNG file."u8
                .ToArray();

        var exception =
            await Assert.ThrowsAsync<
                InvalidSensorAttachmentException>(
                () => service.UploadAsync(
                    Guid.NewGuid(),
                    "deployment.png",
                    "image/png",
                    SensorAttachmentCategory.DeploymentPhoto,
                    new MemoryStream(payload)));

        Assert.Contains(
            "PNG",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            store.SaveCallCount);
    }

    [Fact]
    public async Task UploadAsync_WithValidJpegSignature_AcceptsPhoto()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        byte[] payload =
        [
            0xFF,
            0xD8,
            0xFF,
            0xE0,
            0x00,
            0x10,
            0x4A,
            0x46,
            0x49,
            0x46
        ];

        await service.UploadAsync(
            Guid.NewGuid(),
            "deployment.jpg",
            "image/jpeg",
            SensorAttachmentCategory.DeploymentPhoto,
            new MemoryStream(payload));

        Assert.Equal(
            1,
            store.SaveCallCount);
    }

    [Fact]
    public async Task UploadAsync_WithValidWebPSignature_AcceptsPhoto()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        byte[] payload =
        [
            0x52,
            0x49,
            0x46,
            0x46,
            0x10,
            0x00,
            0x00,
            0x00,
            0x57,
            0x45,
            0x42,
            0x50
        ];

        await service.UploadAsync(
            Guid.NewGuid(),
            "deployment.webp",
            "image/webp",
            SensorAttachmentCategory.DeploymentPhoto,
            new MemoryStream(payload));

        Assert.Equal(
            1,
            store.SaveCallCount);
    }

    [Fact]
    public async Task UploadAsync_WhenPhotoMimeDoesNotMatchExtension_RejectsFile()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        byte[] jpegPayload =
        [
            0xFF,
            0xD8,
            0xFF,
            0xE0
        ];

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "pretending-to-be-png.png",
                "image/jpeg",
                SensorAttachmentCategory.DeploymentPhoto,
                new MemoryStream(jpegPayload)));

        Assert.Equal(
            0,
            store.SaveCallCount);
    }

    [Fact]
    public async Task UploadAsync_WhenContentEmpty_RejectsBeforePersistence()
    {
        var store =
            new RecordingAttachmentStore();

        var service =
            CreateService(store);

        var exception =
            await Assert.ThrowsAsync<
                InvalidSensorAttachmentException>(
                () => service.UploadAsync(
                    Guid.NewGuid(),
                    "gateway.log",
                    "text/plain",
                    SensorAttachmentCategory.HardwareLog,
                    new MemoryStream()));

        Assert.Contains(
            "empty",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            store.SaveCallCount);
    }

    private static SensorAttachmentService CreateService(
        RecordingAttachmentStore store)
    {
        return new SensorAttachmentService(
            store,
            new FixedTimeProvider(
                FixedTime));
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

    private sealed class RecordingAttachmentStore
        : ISensorAttachmentStore
    {
        public int SaveCallCount
        {
            get;
            private set;
        }

        public byte[] StoredContent
        {
            get;
            private set;
        } = [];

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

            StoredContent =
                destination.ToArray();

            return new SensorAttachmentMetadata(
                Guid.NewGuid(),
                request.SensorProfileId,
                request.FileName,
                request.ContentType,
                request.Category,
                StoredContent.LongLength,
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