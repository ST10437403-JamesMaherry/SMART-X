using System.Security.Cryptography;
using System.Text;

using SmartX.Infrastructure.Attachments;

namespace SmartX.Infrastructure.Tests.Attachments;

public sealed class ChunkedAesGcmAttachmentCipherTests
{
    private static readonly string TestKeyBase64 =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    [Fact]
    public async Task EncryptThenDecrypt_WhenValid_RestoresOriginalPayload()
    {
        var payload =
            Encoding.UTF8.GetBytes(
                "Smart-X gateway hardware log entry.");

        using var cipher =
            new ChunkedAesGcmAttachmentCipher(
                TestKeyBase64);

        using var plaintext =
            new MemoryStream(payload);

        using var encrypted =
            new MemoryStream();

        var encryptedByteCount =
            await cipher.EncryptAsync(
                plaintext,
                encrypted);

        Assert.Equal(
            payload.LongLength,
            encryptedByteCount);

        encrypted.Position = 0;

        using var decrypted =
            new MemoryStream();

        await cipher.DecryptAsync(
            encrypted,
            decrypted);

        Assert.Equal(
            payload,
            decrypted.ToArray());
    }

    [Fact]
    public async Task EncryptAsync_WhenValid_DoesNotPersistPlaintext()
    {
        var payload =
            Encoding.UTF8.GetBytes(
                "DISTINCTIVE-SMARTX-SECRET-TELEMETRY-LOG");

        using var cipher =
            new ChunkedAesGcmAttachmentCipher(
                TestKeyBase64);

        using var plaintext =
            new MemoryStream(payload);

        using var encrypted =
            new MemoryStream();

        await cipher.EncryptAsync(
            plaintext,
            encrypted);

        var encryptedBytes =
            encrypted.ToArray();

        Assert.False(
            payload.SequenceEqual(encryptedBytes));

        Assert.DoesNotContain(
            "DISTINCTIVE-SMARTX-SECRET-TELEMETRY-LOG",
            Encoding.UTF8.GetString(encryptedBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecryptAsync_WhenCiphertextModified_RejectsTamperedData()
    {
        var payload =
            Encoding.UTF8.GetBytes(
                "Authenticated Smart-X attachment payload.");

        using var cipher =
            new ChunkedAesGcmAttachmentCipher(
                TestKeyBase64);

        using var plaintext =
            new MemoryStream(payload);

        using var encrypted =
            new MemoryStream();

        await cipher.EncryptAsync(
            plaintext,
            encrypted);

        var encryptedBytes =
            encrypted.ToArray();

        // Header = 20 bytes and chunk length = 4 bytes.
        // Modify the first ciphertext byte without changing the encrypted
        // attachment's structural format. AES-GCM authentication must detect
        // that the ciphertext no longer matches its authentication tag.
        encryptedBytes[24] ^= 0x01;

        using var tampered =
            new MemoryStream(encryptedBytes);

        using var decrypted =
            new MemoryStream();

        // AuthenticationTagMismatchException derives from
        // CryptographicException. ThrowsAnyAsync verifies the public
        // cryptographic failure contract without incorrectly requiring the
        // base exception to be thrown directly.
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => cipher.DecryptAsync(
                tampered,
                decrypted));
    }

    [Fact]
    public async Task DecryptAsync_WhenFinalRecordTruncated_RejectsPayload()
    {
        var payload =
            Encoding.UTF8.GetBytes(
                "Smart-X protected configuration.");

        using var cipher =
            new ChunkedAesGcmAttachmentCipher(
                TestKeyBase64);

        using var plaintext =
            new MemoryStream(payload);

        using var encrypted =
            new MemoryStream();

        await cipher.EncryptAsync(
            plaintext,
            encrypted);

        var encryptedBytes =
            encrypted.ToArray();

        var truncated =
            encryptedBytes[..^1];

        using var truncatedStream =
            new MemoryStream(truncated);

        using var decrypted =
            new MemoryStream();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => cipher.DecryptAsync(
                truncatedStream,
                decrypted));
    }

    [Fact]
    public void Constructor_WhenKeyInvalid_ThrowsMeaningfulException()
    {
        var exception =
            Assert.Throws<InvalidOperationException>(
                () => new ChunkedAesGcmAttachmentCipher(
                    "not-valid-base64"));

        Assert.Contains(
            "Base64",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}