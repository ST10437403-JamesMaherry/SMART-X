namespace SmartX.Contracts.Attachments;

/// <summary>
/// API representation of stored Smart-X sensor attachment metadata.
/// </summary>
public sealed record SensorAttachmentResponse(
    Guid Id,
    Guid SensorProfileId,
    string FileName,
    string ContentType,
    string Category,
    long SizeBytes,
    DateTimeOffset UploadedAtUtc);