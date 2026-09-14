namespace SmartX.Application.Attachments;

/// <summary>
/// Defines the persistence operations required by the sensor attachment
/// application service.
///
/// The interface deliberately exposes streams rather than byte arrays so
/// that large attachments can move through the application without requiring
/// the complete file to exist in memory.
/// </summary>
public interface ISensorAttachmentStore
{
    Task<SensorAttachmentMetadata> SaveAsync(
        SensorAttachmentWriteRequest request,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default);

    Task<SensorAttachmentMetadata?> GetMetadataAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    Task WriteContentAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default);
}