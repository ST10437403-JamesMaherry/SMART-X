using SmartX.Application.Attachments;

namespace SmartX.Application.Tests.Attachments;

public sealed class SensorAttachmentServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 9, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UploadAsync_WhenValid_PersistsNormalisedAttachment()
    {
        var store = new RecordingAttachmentStore();

        var service = CreateService(store);

        var sensorId = Guid.NewGuid();

        var content = new MemoryStream(
            "gateway started successfully"u8.ToArray());

        var result = await service.UploadAsync(
            sensorId,
            " gateway.log ",
            "text/plain; charset=utf-8",
            SensorAttachmentCategory.HardwareLog,
            content);

        Assert.Equal(sensorId, result.SensorProfileId);
        Assert.Equal("gateway.log", result.FileName);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal(
            SensorAttachmentCategory.HardwareLog,
            result.Category);
        Assert.Equal(FixedUtcNow, result.UploadedAtUtc);

        Assert.NotNull(store.LastRequest);
        Assert.Equal(
            "gateway.log",
            store.LastRequest.FileName);
    }

    [Fact]
    public async Task UploadAsync_WhenSensorIdEmpty_ThrowsMeaningfulException()
    {
        var service = CreateService(
            new RecordingAttachmentStore());

        var exception =
            await Assert.ThrowsAsync<
                InvalidSensorAttachmentException>(
                () => service.UploadAsync(
                    Guid.Empty,
                    "gateway.log",
                    "text/plain",
                    SensorAttachmentCategory.HardwareLog,
                    new MemoryStream([1, 2, 3])));

        Assert.Contains(
            "sensor profile ID",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAsync_WhenCategoryUndefined_ThrowsMeaningfulException()
    {
        var service = CreateService(
            new RecordingAttachmentStore());

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "gateway.log",
                "text/plain",
                (SensorAttachmentCategory)999,
                new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task UploadAsync_WhenFileNameContainsPath_RejectsTraversal()
    {
        var service = CreateService(
            new RecordingAttachmentStore());

        var exception =
            await Assert.ThrowsAsync<
                InvalidSensorAttachmentException>(
                () => service.UploadAsync(
                    Guid.NewGuid(),
                    "../gateway.log",
                    "text/plain",
                    SensorAttachmentCategory.HardwareLog,
                    new MemoryStream([1, 2, 3])));

        Assert.Contains(
            "path separators",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAsync_WhenExtensionUnsupported_ThrowsMeaningfulException()
    {
        var service = CreateService(
            new RecordingAttachmentStore());

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "gateway.exe",
                "application/octet-stream",
                SensorAttachmentCategory.HardwareLog,
                new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task UploadAsync_WhenContentTypeDoesNotMatchCategory_ThrowsException()
    {
        var service = CreateService(
            new RecordingAttachmentStore());

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "deployment.png",
                "text/plain",
                SensorAttachmentCategory.DeploymentPhoto,
                new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task UploadAsync_WhenDeclaredSizeTooLarge_RejectsBeforeStorage()
    {
        var store = new RecordingAttachmentStore();

        var service = CreateService(store);

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "gateway.log",
                "text/plain",
                SensorAttachmentCategory.HardwareLog,
                new MemoryStream([1, 2, 3]),
                SensorAttachmentService.MaxAttachmentSizeBytes + 1));

        Assert.Null(store.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_WhenActualStreamExceedsLimit_RejectsDuringRead()
    {
        var store = new RecordingAttachmentStore();

        var service = CreateService(store);

        var oversizedPayload = new byte[
            checked(
                (int)SensorAttachmentService
                    .MaxAttachmentSizeBytes + 1)];

        await Assert.ThrowsAsync<
            InvalidSensorAttachmentException>(
            () => service.UploadAsync(
                Guid.NewGuid(),
                "gateway.log",
                "text/plain",
                SensorAttachmentCategory.HardwareLog,
                new MemoryStream(oversizedPayload)));
    }

    private static SensorAttachmentService CreateService(
        RecordingAttachmentStore store)
    {
        return new SensorAttachmentService(
            store,
            new FixedTimeProvider(FixedUtcNow));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class RecordingAttachmentStore
        : ISensorAttachmentStore
    {
        public SensorAttachmentWriteRequest? LastRequest
        {
            get;
            private set;
        }

        public byte[]? LastContent
        {
            get;
            private set;
        }

        public async Task<SensorAttachmentMetadata> SaveAsync(
            SensorAttachmentWriteRequest request,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            using var buffer = new MemoryStream();

            await content.CopyToAsync(
                buffer,
                cancellationToken);

            LastContent = buffer.ToArray();

            return new SensorAttachmentMetadata(
                Guid.NewGuid(),
                request.SensorProfileId,
                request.FileName,
                request.ContentType,
                request.Category,
                LastContent.LongLength,
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
                SensorAttachmentMetadata?>(null);
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