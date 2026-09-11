namespace SmartX.Application.Attachments;

/// <summary>
/// Describes an attachment that has been safely stored for a Smart-X
/// sensor profile.
///
/// The physical encrypted storage path is deliberately excluded so that
/// infrastructure details never leak into the application or API layers.
/// </summary>
public sealed record SensorAttachmentMetadata(
    Guid Id,
    Guid SensorProfileId,
    string FileName,
    string ContentType,
    SensorAttachmentCategory Category,
    long SizeBytes,
    DateTimeOffset UploadedAtUtc);