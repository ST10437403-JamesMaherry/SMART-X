using SmartX.Application.Attachments;

namespace SmartX.Infrastructure.Attachments;

/// <summary>
/// Defers construction of the concrete encrypted attachment store until an
/// attachment operation is actually performed.
///
/// Adaptive anomaly analysis depends on the ISensorAttachmentStore abstraction
/// for configuration evidence. Without this proxy, merely resolving dashboard
/// analysis for telemetry could unnecessarily instantiate cryptographic
/// infrastructure and require an attachment encryption key.
///
/// Lazy<T> uses ExecutionAndPublication so the underlying singleton store is
/// created at most once even when concurrent requests first access it.
/// </summary>
public sealed class DeferredSensorAttachmentStore
    : ISensorAttachmentStore
{
    private readonly Lazy<ISensorAttachmentStore> _inner;

    public DeferredSensorAttachmentStore(
        Func<ISensorAttachmentStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(
            storeFactory);

        _inner =
            new Lazy<ISensorAttachmentStore>(
                storeFactory,
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<SensorAttachmentMetadata> SaveAsync(
        SensorAttachmentWriteRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        return _inner.Value.SaveAsync(
            request,
            content,
            cancellationToken);
    }

    public Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        return _inner.Value.ListAsync(
            sensorProfileId,
            cancellationToken);
    }

    public Task<SensorAttachmentMetadata?> GetMetadataAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        return _inner.Value.GetMetadataAsync(
            sensorProfileId,
            attachmentId,
            cancellationToken);
    }

    public Task WriteContentAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        return _inner.Value.WriteContentAsync(
            sensorProfileId,
            attachmentId,
            destination,
            cancellationToken);
    }
}