namespace SmartX.Application.Attachments;

/// <summary>
/// Contains validated metadata required by the infrastructure layer when
/// persisting a new sensor attachment.
/// </summary>
public sealed record SensorAttachmentWriteRequest(
    Guid SensorProfileId,
    string FileName,
    string ContentType,
    SensorAttachmentCategory Category,
    DateTimeOffset UploadedAtUtc);