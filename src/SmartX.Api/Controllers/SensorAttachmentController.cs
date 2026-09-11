using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

using SmartX.Api.Filters;
using SmartX.Application.Attachments;
using SmartX.Contracts.Attachments;

namespace SmartX.Api.Controllers;

/// <summary>
/// Provides streaming upload, listing and download endpoints for files
/// attached to Smart-X sensor profiles.
/// </summary>
[ApiController]
[Route("api/sensors/{sensorProfileId:guid}/attachments")]
public sealed class SensorAttachmentsController : ControllerBase
{
    private const int MaxMultipartBoundaryLength = 128;

    // Allow a small amount of multipart/header overhead above the actual
    // application-level 10 MiB attachment limit.
    private const long MaxMultipartBodySizeBytes =
        SensorAttachmentService.MaxAttachmentSizeBytes +
        (1024L * 1024L);

    private readonly SensorAttachmentService _attachmentService;

    public SensorAttachmentsController(
        SensorAttachmentService attachmentService)
    {
        _attachmentService = attachmentService;
    }

    /// <summary>
    /// Uploads one encrypted attachment for a sensor profile.
    ///
    /// The multipart body is parsed directly from Request.Body instead of
    /// using IFormFile, avoiding unnecessary whole-file buffering.
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
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid attachment category",
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
                        contentDisposition.DispositionType.Value,
                        "form-data",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName =
                    HeaderUtilities.RemoveQuotes(
                            contentDisposition.FileNameStar)
                        .Value;

                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName =
                        HeaderUtilities.RemoveQuotes(
                                contentDisposition.FileName)
                            .Value;
                }

                // A form-data section without a filename is a normal
                // field rather than a file section.
                if (string.IsNullOrWhiteSpace(fileName))
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
                        attachmentId = metadata.Id
                    },
                    ToResponse(metadata));
            }

            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Attachment file required",
                detail:
                    "The multipart request did not contain a file.");
        }
        catch (InvalidSensorAttachmentException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid sensor attachment",
                detail: exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Malformed multipart attachment",
                detail: exception.Message);
        }
    }

    /// <summary>
    /// Lists metadata for all attachments associated with a sensor.
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
    /// Streams a decrypted attachment to the requesting client.
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
                FileNameStar = metadata.FileName
            };

        Response.Headers.ContentDisposition =
            contentDisposition.ToString();

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
        if (string.IsNullOrWhiteSpace(contentType) ||
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

        if (string.IsNullOrWhiteSpace(boundary))
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