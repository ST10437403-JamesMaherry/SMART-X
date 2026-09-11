using SmartX.Application.Sensors;

namespace SmartX.Application.Attachments;

/// <summary>
/// Coordinates attachment operations that must belong to an actually
/// registered Smart-X sensor.
///
/// SensorAttachmentService remains responsible for file validation and
/// persistence orchestration, while this service enforces the cross-aggregate
/// rule that attachment resources cannot exist for unknown sensor profiles.
/// </summary>
public sealed class RegisteredSensorAttachmentService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly SensorAttachmentService _attachmentService;

    public RegisteredSensorAttachmentService(
        ISensorRepository sensorRepository,
        SensorAttachmentService attachmentService)
    {
        _sensorRepository =
            sensorRepository ??
            throw new ArgumentNullException(
                nameof(sensorRepository));

        _attachmentService =
            attachmentService ??
            throw new ArgumentNullException(
                nameof(attachmentService));
    }

    /// <summary>
    /// Uploads an attachment only after confirming that its parent sensor
    /// exists.
    /// </summary>
    public async Task<SensorAttachmentMetadata> UploadAsync(
        Guid sensorProfileId,
        string? fileName,
        string? contentType,
        SensorAttachmentCategory category,
        Stream content,
        long? declaredLength = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSensorExistsAsync(
            sensorProfileId,
            cancellationToken);

        return await _attachmentService.UploadAsync(
            sensorProfileId,
            fileName,
            contentType,
            category,
            content,
            declaredLength,
            cancellationToken);
    }

    /// <summary>
    /// Lists attachments only for a registered sensor profile.
    /// </summary>
    public async Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSensorExistsAsync(
            sensorProfileId,
            cancellationToken);

        return await _attachmentService.ListAsync(
            sensorProfileId,
            cancellationToken);
    }

    /// <summary>
    /// Retrieves attachment metadata only when the parent sensor exists.
    /// </summary>
    public async Task<SensorAttachmentMetadata?> GetMetadataAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSensorExistsAsync(
            sensorProfileId,
            cancellationToken);

        return await _attachmentService.GetMetadataAsync(
            sensorProfileId,
            attachmentId,
            cancellationToken);
    }

    /// <summary>
    /// Streams decrypted content only when the parent sensor exists.
    /// </summary>
    public async Task WriteContentAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        await EnsureSensorExistsAsync(
            sensorProfileId,
            cancellationToken);

        await _attachmentService.WriteContentAsync(
            sensorProfileId,
            attachmentId,
            destination,
            cancellationToken);
    }

    private async Task EnsureSensorExistsAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidSensorAttachmentException(
                "A valid sensor profile ID is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }
    }
}