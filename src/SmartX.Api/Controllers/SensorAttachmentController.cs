using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

using SmartX.Api.Filters;
using SmartX.Application.Attachments;
using SmartX.Contracts.Attachments;

namespace SmartX.Api.Controllers;

/// <summary>
/// Provides streaming upload, listing and download endpoints for files
/// attached to registered Smart-X sensor profiles.
/// </summary>
[ApiController]
[Route("api/sensors/{sensorProfileId:guid}/attachments")]
public sealed class SensorAttachmentsController : ControllerBase
{
    private const int MaxMultipartBoundaryLength = 128;

    // Allow multipart/header overhead above the application-level attachment
    // limit while the streamed attachment itself remains limited to 10 MiB.
    private const long MaxMultipartBodySizeBytes =
        SensorAttachmentService.MaxAttachmentSizeBytes +
        (1024L * 1024L);

    private readonly RegisteredSensorAttachmentService _attachmentService;

    public SensorAttachmentsController(
        RegisteredSensorAttachmentService attachmentService)
    {
        _attachmentService =
            attachmentService ??
            throw new ArgumentNullException(
                nameof(attachmentService));
    }

    /// <summary>
    /// Streams one attachment from the multipart request directly into the
    /// validated and encrypted Smart-X attachment pipeline.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [DisableFormValueModelBinding]
    [RequestSizeLimit(MaxMultipartBodySizeBytes)]
    public async Task<IActionResult> Upload(
        Guid sensorProfileId,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        if (!TryParseCategory(
                category,
                out var attachmentCategory))
        {
            return Problem(
                statusCode:
                    StatusCodes.Status400BadRequest,
                title:
                    "Invalid attachment category",
                detail:
                    "The category must be Configuration, " +
                    "DeploymentPhoto or HardwareLog.");
        }

        try
        {
            var boundary =
                GetMultipartBoundary(
                    Request.ContentType);

            var reader =
                new MultipartReader(
                    boundary,
                    Request.Body);

            MultipartSection? section;

            while ((section =
                await reader.ReadNextSectionAsync(
                    cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out var contentDisposition))
                {
                    continue;
                }

                if (!string.Equals(
                        contentDisposition
                            .DispositionType
                            .Value,
                        "form-data",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName =
                    HeaderUtilities.RemoveQuotes(
                            contentDisposition.FileNameStar)
                        .Value;

                if (string.IsNullOrWhiteSpace(
                        fileName))
                {
                    fileName =
                        HeaderUtilities.RemoveQuotes(
                                contentDisposition.FileName)
                            .Value;
                }

                // A multipart form-data section without a filename is an
                // ordinary form field rather than an uploaded file.
                if (string.IsNullOrWhiteSpace(
                        fileName))
                {
                    continue;
                }

                var metadata =
                    await _attachmentService.UploadAsync(
                        sensorProfileId,
                        fileName,
                        section.ContentType ??
                        "application/octet-stream",
                        attachmentCategory,
                        section.Body,
                        cancellationToken:
                            cancellationToken);

                return CreatedAtAction(
                    nameof(Download),
                    new
                    {
                        sensorProfileId,
                        attachmentId =
                            metadata.Id
                    },
                    ToResponse(metadata));
            }

            return Problem(
                statusCode:
                    StatusCodes.Status400BadRequest,
                title:
                    "Attachment file required",
                detail:
                    "The multipart request did not contain a file.");
        }
        catch (InvalidSensorAttachmentException exception)
        {
            return Problem(
                statusCode:
                    StatusCodes.Status400BadRequest,
                title:
                    "Invalid sensor attachment",
                detail:
                    exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Problem(
                statusCode:
                    StatusCodes.Status400BadRequest,
                title:
                    "Malformed multipart attachment",
                detail:
                    exception.Message);
        }
    }

    /// <summary>
    /// Lists attachment metadata for a registered sensor.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<
        IReadOnlyList<SensorAttachmentResponse>>> List(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
    {
        var attachments =
            await _attachmentService.ListAsync(
                sensorProfileId,
                cancellationToken);

        var response =
            attachments
                .Select(ToResponse)
                .ToArray();

        return Ok(response);
    }

    /// <summary>
    /// Streams decrypted attachment content to the requesting client without
    /// writing a plaintext temporary file to persistent storage.
    /// </summary>
    [HttpGet("{attachmentId:guid}")]
    public async Task<IActionResult> Download(
        Guid sensorProfileId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var metadata =
            await _attachmentService.GetMetadataAsync(
                sensorProfileId,
                attachmentId,
                cancellationToken);

        if (metadata is null)
        {
            return NotFound();
        }

        Response.ContentType =
            metadata.ContentType;

        Response.ContentLength =
            metadata.SizeBytes;

        var contentDisposition =
            new System.Net.Http.Headers
                .ContentDispositionHeaderValue(
                    "attachment")
            {
                FileNameStar =
                    metadata.FileName
            };

        Response.Headers.ContentDisposition =
            contentDisposition.ToString();

        // Sensitive diagnostic/configuration files should not be cached by
        // browsers or intermediary HTTP caches.
        Response.Headers["Cache-Control"] =
            "no-store";

        Response.Headers["X-Content-Type-Options"] =
            "nosniff";

        await _attachmentService.WriteContentAsync(
            sensorProfileId,
            attachmentId,
            Response.Body,
            cancellationToken);

        return new EmptyResult();
    }

    private static bool TryParseCategory(
        string? value,
        out SensorAttachmentCategory category)
    {
        if (!Enum.TryParse(
                value,
                ignoreCase: true,
                out category))
        {
            return false;
        }

        return Enum.IsDefined(
            typeof(SensorAttachmentCategory),
            category);
    }

    private static string GetMultipartBoundary(
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(
                contentType) ||
            !MediaTypeHeaderValue.TryParse(
                contentType,
                out var mediaType) ||
            !string.Equals(
                mediaType.MediaType.Value,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidSensorAttachmentException(
                "The request must use multipart/form-data.");
        }

        var boundary =
            HeaderUtilities.RemoveQuotes(
                    mediaType.Boundary)
                .Value;

        if (string.IsNullOrWhiteSpace(
                boundary))
        {
            throw new InvalidSensorAttachmentException(
                "The multipart request boundary is missing.");
        }

        if (boundary.Length >
            MaxMultipartBoundaryLength)
        {
            throw new InvalidSensorAttachmentException(
                "The multipart request boundary is too long.");
        }

        return boundary;
    }

    private static SensorAttachmentResponse ToResponse(
        SensorAttachmentMetadata metadata)
    {
        return new SensorAttachmentResponse(
            metadata.Id,
            metadata.SensorProfileId,
            metadata.FileName,
            metadata.ContentType,
            metadata.Category.ToString(),
            metadata.SizeBytes,
            metadata.UploadedAtUtc);
    }
}