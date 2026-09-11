namespace SmartX.Application.Attachments;

/// <summary>
/// Performs lightweight content-signature inspection for binary sensor
/// attachments.
///
/// Client-provided file names and Content-Type headers are not trusted as
/// proof of the actual file format. Binary deployment photographs therefore
/// have their well-known leading bytes checked before persistent storage.
///
/// Text-based configuration and log formats do not have universal magic
/// numbers, so their protection continues to rely on the existing extension,
/// media-type, file-name and size validation rules.
/// </summary>
internal static class AttachmentSignatureValidator
{
    /// <summary>
    /// Maximum number of leading bytes required by the supported signatures.
    /// WebP requires twelve bytes to validate both RIFF and WEBP markers.
    /// </summary>
    public const int InspectionPrefixLength = 12;

    private static readonly byte[] JpegSignature =
    [
        0xFF,
        0xD8,
        0xFF
    ];

    private static readonly byte[] PngSignature =
    [
        0x89,
        0x50,
        0x4E,
        0x47,
        0x0D,
        0x0A,
        0x1A,
        0x0A
    ];

    private static readonly byte[] RiffSignature =
        "RIFF"u8.ToArray();

    private static readonly byte[] WebpSignature =
        "WEBP"u8.ToArray();

    /// <summary>
    /// Validates the actual binary signature when the attachment format has a
    /// stable magic-byte representation.
    /// </summary>
    public static void Validate(
        SensorAttachmentCategory category,
        string fileName,
        string contentType,
        ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            throw new InvalidSensorAttachmentException(
                "Empty attachment files are not supported.");
        }

        // Configuration and hardware-log formats are predominantly text
        // formats and do not share a reliable universal binary signature.
        if (category != SensorAttachmentCategory.DeploymentPhoto)
        {
            return;
        }

        var extension =
            Path.GetExtension(fileName)
                .ToLowerInvariant();

        switch (extension)
        {
            case ".jpg":
            case ".jpeg":
                ValidateJpeg(
                    contentType,
                    prefix);
                break;

            case ".png":
                ValidatePng(
                    contentType,
                    prefix);
                break;

            case ".webp":
                ValidateWebP(
                    contentType,
                    prefix);
                break;

            default:
                // The normal attachment rules should already reject an
                // unsupported image extension. Keep this defensive guard so
                // signature validation remains safe when used independently.
                throw new InvalidSensorAttachmentException(
                    $"Files with the '{extension}' extension are not " +
                    "supported deployment photographs.");
        }
    }

    private static void ValidateJpeg(
        string contentType,
        ReadOnlySpan<byte> prefix)
    {
        EnsureContentType(
            contentType,
            "image/jpeg",
            "JPEG");

        if (!StartsWith(
                prefix,
                JpegSignature))
        {
            throw new InvalidSensorAttachmentException(
                "The uploaded JPEG file does not contain a valid JPEG " +
                "file signature.");
        }
    }

    private static void ValidatePng(
        string contentType,
        ReadOnlySpan<byte> prefix)
    {
        EnsureContentType(
            contentType,
            "image/png",
            "PNG");

        if (!StartsWith(
                prefix,
                PngSignature))
        {
            throw new InvalidSensorAttachmentException(
                "The uploaded PNG file does not contain a valid PNG " +
                "file signature.");
        }
    }

    private static void ValidateWebP(
        string contentType,
        ReadOnlySpan<byte> prefix)
    {
        EnsureContentType(
            contentType,
            "image/webp",
            "WebP");

        // WebP is stored inside a RIFF container:
        //
        // bytes 0-3   = RIFF
        // bytes 4-7   = file size
        // bytes 8-11  = WEBP
        if (prefix.Length < InspectionPrefixLength ||
            !prefix[..4].SequenceEqual(
                RiffSignature) ||
            !prefix.Slice(8, 4).SequenceEqual(
                WebpSignature))
        {
            throw new InvalidSensorAttachmentException(
                "The uploaded WebP file does not contain a valid WebP " +
                "file signature.");
        }
    }

    private static void EnsureContentType(
        string actual,
        string expected,
        string formatName)
    {
        if (!string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidSensorAttachmentException(
                $"A {formatName} deployment photograph must use the " +
                $"'{expected}' content type.");
        }
    }

    private static bool StartsWith(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> expected)
    {
        return value.Length >= expected.Length &&
               value[..expected.Length].SequenceEqual(expected);
    }
}