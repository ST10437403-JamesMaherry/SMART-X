using System.Text.Json;
using System.Text.Json.Serialization;

using SmartX.Application.Attachments;

namespace SmartX.Infrastructure.Attachments;

/// <summary>
/// Persists encrypted Smart-X sensor attachments to the local filesystem.
///
/// User-supplied file names are never used as physical storage names.
/// Generated GUIDs are used instead, preventing path traversal and storage
/// collisions.
///
/// Payloads are encrypted before reaching persistent storage.
/// </summary>
public sealed class FileSystemSensorAttachmentStore
    : ISensorAttachmentStore
{
    private const string PayloadSuffix =
        ".payload";

    private const string MetadataSuffix =
        ".metadata.json";

    private const int StreamBufferSize =
        64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    private readonly string _rootPath;
    private readonly IAttachmentCipher _cipher;

    public FileSystemSensorAttachmentStore(
        string rootPath,
        IAttachmentCipher cipher)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException(
                "An attachment storage root is required.",
                nameof(rootPath));
        }

        _rootPath =
            Path.GetFullPath(rootPath);

        _cipher = cipher
            ?? throw new ArgumentNullException(nameof(cipher));

        Directory.CreateDirectory(_rootPath);
    }

    public async Task<SensorAttachmentMetadata> SaveAsync(
        SensorAttachmentWriteRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        var attachmentId =
            Guid.NewGuid();

        var sensorDirectory =
            GetSensorDirectory(
                request.SensorProfileId);

        Directory.CreateDirectory(
            sensorDirectory);

        var payloadPath =
            GetPayloadPath(
                request.SensorProfileId,
                attachmentId);

        var metadataPath =
            GetMetadataPath(
                request.SensorProfileId,
                attachmentId);

        var temporaryToken =
            Guid.NewGuid().ToString("N");

        var temporaryPayloadPath =
            $"{payloadPath}.{temporaryToken}.tmp";

        var temporaryMetadataPath =
            $"{metadataPath}.{temporaryToken}.tmp";

        try
        {
            long plaintextSize;

            await using (var encryptedOutput =
                new FileStream(
                    temporaryPayloadPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    StreamBufferSize,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan))
            {
                plaintextSize =
                    await _cipher.EncryptAsync(
                        content,
                        encryptedOutput,
                        cancellationToken);

                await encryptedOutput.FlushAsync(
                    cancellationToken);
            }

            var metadata =
                new SensorAttachmentMetadata(
                    attachmentId,
                    request.SensorProfileId,
                    request.FileName,
                    request.ContentType,
                    request.Category,
                    plaintextSize,
                    request.UploadedAtUtc);

            await using (var metadataOutput =
                new FileStream(
                    temporaryMetadataPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    StreamBufferSize,
                    FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    metadataOutput,
                    metadata,
                    SerializerOptions,
                    cancellationToken);

                await metadataOutput.FlushAsync(
                    cancellationToken);
            }

            // Move fully completed temporary files into their final
            // locations. This avoids exposing partially written payloads.
            File.Move(
                temporaryPayloadPath,
                payloadPath);

            File.Move(
                temporaryMetadataPath,
                metadataPath);

            return metadata;
        }
        catch
        {
            SafeDelete(temporaryPayloadPath);
            SafeDelete(temporaryMetadataPath);
            SafeDelete(payloadPath);
            SafeDelete(metadataPath);

            throw;
        }
    }

    public async Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        var sensorDirectory =
            GetSensorDirectory(sensorProfileId);

        if (!Directory.Exists(sensorDirectory))
        {
            return Array.Empty<SensorAttachmentMetadata>();
        }

        var metadata =
            new List<SensorAttachmentMetadata>();

        foreach (var metadataPath in Directory.EnumerateFiles(
            sensorDirectory,
            $"*{MetadataSuffix}",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var storedFileName =
                Path.GetFileName(metadataPath);

            var idText =
                storedFileName[
                    ..^MetadataSuffix.Length];

            if (!Guid.TryParseExact(
                    idText,
                    "N",
                    out var attachmentId))
            {
                throw new InvalidDataException(
                    $"Attachment metadata file '{storedFileName}' " +
                    "does not contain a valid attachment ID.");
            }

            var item =
                await GetMetadataAsync(
                    sensorProfileId,
                    attachmentId,
                    cancellationToken);

            if (item is not null)
            {
                metadata.Add(item);
            }
        }

        return metadata
            .OrderByDescending(item => item.UploadedAtUtc)
            .ThenBy(
                item => item.FileName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SensorAttachmentMetadata?> GetMetadataAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var metadataPath =
            GetMetadataPath(
                sensorProfileId,
                attachmentId);

        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream =
            new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

        var metadata =
            await JsonSerializer.DeserializeAsync<
                SensorAttachmentMetadata>(
                    stream,
                    SerializerOptions,
                    cancellationToken);

        if (metadata is null)
        {
            throw new InvalidDataException(
                "Stored sensor attachment metadata is empty.");
        }

        if (metadata.SensorProfileId != sensorProfileId ||
            metadata.Id != attachmentId)
        {
            throw new InvalidDataException(
                "Stored sensor attachment metadata does not match its " +
                "physical storage location.");
        }

        return metadata;
    }

    public async Task WriteContentAsync(
        Guid sensorProfileId,
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var payloadPath =
            GetPayloadPath(
                sensorProfileId,
                attachmentId);

        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException(
                "The requested encrypted sensor attachment does not exist.",
                payloadPath);
        }

        await using var encryptedInput =
            new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

        await _cipher.DecryptAsync(
            encryptedInput,
            destination,
            cancellationToken);
    }

    private string GetSensorDirectory(
        Guid sensorProfileId)
    {
        return Path.Combine(
            _rootPath,
            sensorProfileId.ToString("N"));
    }

    private string GetPayloadPath(
        Guid sensorProfileId,
        Guid attachmentId)
    {
        return Path.Combine(
            GetSensorDirectory(sensorProfileId),
            $"{attachmentId:N}{PayloadSuffix}");
    }

    private string GetMetadataPath(
        Guid sensorProfileId,
        Guid attachmentId)
    {
        return Path.Combine(
            GetSensorDirectory(sensorProfileId),
            $"{attachmentId:N}{MetadataSuffix}");
    }

    private static void SafeDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must never hide the original persistence failure.
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options =
            new JsonSerializerOptions(
                JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };

        options.Converters.Add(
            new JsonStringEnumConverter());

        return options;
    }
}