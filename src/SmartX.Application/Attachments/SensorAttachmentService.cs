namespace SmartX.Application.Attachments;

/// <summary>
/// Coordinates validation and persistence of files attached to Smart-X
/// sensor profiles.
///
/// HTTP, filesystem and encryption concerns are intentionally kept outside
/// this service so the Application layer remains independently testable.
/// </summary>
public sealed class SensorAttachmentService
{
    public const long MaxAttachmentSizeBytes =
        10L * 1024L * 1024L;

    public const int MaxFileNameLength = 180;

    private static readonly IReadOnlyDictionary<
        SensorAttachmentCategory,
        AttachmentRules> Rules =
        new Dictionary<SensorAttachmentCategory, AttachmentRules>
        {
            [SensorAttachmentCategory.Configuration] =
                new(
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ".json",
                        ".yaml",
                        ".yml",
                        ".cfg",
                        ".txt"
                    },
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        "application/json",
                        "application/yaml",
                        "text/yaml",
                        "text/plain",
                        "application/octet-stream"
                    }),

            [SensorAttachmentCategory.DeploymentPhoto] =
                new(
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ".jpg",
                        ".jpeg",
                        ".png",
                        ".webp"
                    },
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        "image/jpeg",
                        "image/png",
                        "image/webp"
                    }),

            [SensorAttachmentCategory.HardwareLog] =
                new(
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ".log",
                        ".txt",
                        ".json",
                        ".csv"
                    },
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        "text/plain",
                        "application/json",
                        "text/csv",
                        "application/octet-stream"
                    })
        };

    private readonly ISensorAttachmentStore _store;
    private readonly TimeProvider _timeProvider;

    public SensorAttachmentService(
        ISensorAttachmentStore store,
        TimeProvider timeProvider)
    {
        _store =
            store ??
            throw new ArgumentNullException(
                nameof(store));

        _timeProvider =
            timeProvider ??
            throw new ArgumentNullException(
                nameof(timeProvider));
    }

    /// <summary>
    /// Validates and persists a new sensor attachment.
    ///
    /// Only a small leading prefix is consumed for content inspection.
    /// PrefixReplayingReadStream then restores those bytes to the downstream
    /// pipeline so the complete attachment can remain fully streaming.
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
        ValidateSensorProfileId(
            sensorProfileId);

        if (!Enum.IsDefined(
                typeof(SensorAttachmentCategory),
                category))
        {
            throw new InvalidSensorAttachmentException(
                $"'{category}' is not a supported attachment category.");
        }

        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new InvalidSensorAttachmentException(
                "The supplied attachment content is not readable.");
        }

        if (declaredLength is < 0)
        {
            throw new InvalidSensorAttachmentException(
                "Attachment length cannot be negative.");
        }

        if (declaredLength > MaxAttachmentSizeBytes)
        {
            throw new InvalidSensorAttachmentException(
                $"The attachment exceeds the maximum allowed size of " +
                $"{MaxAttachmentSizeBytes} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var validatedFileName =
            ValidateAndNormaliseFileName(
                fileName);

        var validatedContentType =
            ValidateAndNormaliseContentType(
                contentType);

        ValidateCategoryRules(
            category,
            validatedFileName,
            validatedContentType);

        // Inspect only enough bytes to identify supported binary formats.
        // The complete upload is never buffered in memory.
        var prefix =
            await ReadInspectionPrefixAsync(
                content,
                cancellationToken);

        AttachmentSignatureValidator.Validate(
            category,
            validatedFileName,
            validatedContentType,
            prefix);

        var request =
            new SensorAttachmentWriteRequest(
                sensorProfileId,
                validatedFileName,
                validatedContentType,
                category,
                _timeProvider
                    .GetUtcNow()
                    .ToUniversalTime());

        // Signature inspection consumed the leading bytes from the original
        // stream. Replay them so the encrypted persistent payload remains
        // byte-for-byte equivalent to the original upload.
        using var replayingContent =
            new PrefixReplayingReadStream(
                prefix,
                content,
                leaveOpen: true);

        // This protects against omitted or falsified Content-Length values.
        // Importantly, the limiter wraps the replay stream, meaning the bytes
        // inspected above are still included in the actual size limit.
        using var limitedContent =
            new SizeLimitedReadStream(
                replayingContent,
                MaxAttachmentSizeBytes,
                leaveOpen: true);

        return await _store.SaveAsync(
            request,
            limitedContent,
            cancellationToken);
    }

    /// <summary>
    /// Returns metadata for all attachments associated with a sensor.
    /// </summary>
    public Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(
            sensorProfileId);

        return _store.ListAsync(
            sensorProfileId,
            cancellationToken);
    }

    /// <summary>
    /// Returns metadata for one sensor attachment when it exists.
    /// </summary>
    public Task<SensorAttachmentMetadata?> GetMetadataAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(
            sensorProfileId);

        ValidateAttachmentId(
            attachmentId);

        return _store.GetMetadataAsync(
            sensorProfileId,
            attachmentId,
            cancellationToken);
    }

    /// <summary>
    /// Writes decrypted attachment content directly to the supplied
    /// destination stream.
    /// </summary>
    public Task WriteContentAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ValidateSensorProfileId(
            sensorProfileId);

        ValidateAttachmentId(
            attachmentId);

        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The attachment destination must be writable.",
                nameof(destination));
        }

        return _store.WriteContentAsync(
            sensorProfileId,
            attachmentId,
            destination,
            cancellationToken);
    }

    private static async Task<byte[]> ReadInspectionPrefixAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        var buffer =
            new byte[
                AttachmentSignatureValidator
                    .InspectionPrefixLength];

        var totalBytesRead =
            0;

        while (totalBytesRead <
               buffer.Length)
        {
            var bytesRead =
                await content.ReadAsync(
                    buffer.AsMemory(
                        totalBytesRead,
                        buffer.Length -
                        totalBytesRead),
                    cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead +=
                bytesRead;
        }

        if (totalBytesRead == 0)
        {
            throw new InvalidSensorAttachmentException(
                "Empty attachment files are not supported.");
        }

        if (totalBytesRead ==
            buffer.Length)
        {
            return buffer;
        }

        return buffer[
            ..totalBytesRead];
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidSensorAttachmentException(
                "A valid sensor profile ID is required.");
        }
    }

    private static void ValidateAttachmentId(
        Guid attachmentId)
    {
        if (attachmentId == Guid.Empty)
        {
            throw new InvalidSensorAttachmentException(
                "A valid attachment ID is required.");
        }
    }

    private static string ValidateAndNormaliseFileName(
        string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidSensorAttachmentException(
                "An attachment file name is required.");
        }

        var normalised =
            fileName.Trim();

        if (normalised.Length >
            MaxFileNameLength)
        {
            throw new InvalidSensorAttachmentException(
                $"An attachment file name cannot exceed " +
                $"{MaxFileNameLength} characters.");
        }

        if (normalised.Any(
                char.IsControl))
        {
            throw new InvalidSensorAttachmentException(
                "The attachment file name contains invalid control " +
                "characters.");
        }

        // Uploaded names remain metadata only and must never be interpreted
        // as filesystem paths.
        if (normalised.Contains(
                '/',
                StringComparison.Ordinal) ||
            normalised.Contains(
                '\\',
                StringComparison.Ordinal))
        {
            throw new InvalidSensorAttachmentException(
                "Attachment file names cannot contain path separators.");
        }

        var extension =
            Path.GetExtension(
                normalised);

        if (string.IsNullOrWhiteSpace(
                extension))
        {
            throw new InvalidSensorAttachmentException(
                "The attachment must include a supported file extension.");
        }

        return normalised;
    }

    private static string ValidateAndNormaliseContentType(
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(
                contentType))
        {
            throw new InvalidSensorAttachmentException(
                "An attachment content type is required.");
        }

        var mediaType =
            contentType
                .Split(
                    ';',
                    2)[0]
                .Trim()
                .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(
                mediaType))
        {
            throw new InvalidSensorAttachmentException(
                "The attachment content type is invalid.");
        }

        return mediaType;
    }

    private static void ValidateCategoryRules(
        SensorAttachmentCategory category,
        string fileName,
        string contentType)
    {
        var rules =
            Rules[category];

        var extension =
            Path.GetExtension(
                fileName);

        if (!rules.Extensions.Contains(
                extension))
        {
            throw new InvalidSensorAttachmentException(
                $"Files with the '{extension}' extension are not " +
                $"supported for {category} attachments.");
        }

        if (!rules.ContentTypes.Contains(
                contentType))
        {
            throw new InvalidSensorAttachmentException(
                $"The content type '{contentType}' is not supported for " +
                $"{category} attachments.");
        }
    }

    private sealed record AttachmentRules(
        HashSet<string> Extensions,
        HashSet<string> ContentTypes);
}