using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SmartX.Infrastructure.Attachments;

/// <summary>
/// Provides authenticated streaming encryption for Smart-X attachments
/// using AES-256-GCM.
///
/// Standard AesGcm operates on complete buffers. To avoid buffering an
/// entire uploaded file in memory, Smart-X encrypts fixed-size chunks
/// independently. Every chunk receives a unique nonce and authentication
/// tag.
///
/// A final authenticated marker protects against ciphertext truncation.
/// </summary>
public sealed class ChunkedAesGcmAttachmentCipher
    : IAttachmentCipher,
      IDisposable
{
    private const int RequiredKeySizeBytes = 32;

    private const int NoncePrefixSizeBytes = 8;

    private const int NonceSizeBytes = 12;

    private const int AuthenticationTagSizeBytes = 16;

    private const int ChunkSizeBytes = 64 * 1024;

    private const int LengthFieldSizeBytes = sizeof(int);

    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("SXATT001");

    private static readonly int HeaderSizeBytes =
        Magic.Length +
        NoncePrefixSizeBytes +
        sizeof(int);

    private readonly byte[] _key;

    private bool _disposed;

    public ChunkedAesGcmAttachmentCipher(
        string? encryptionKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptionKeyBase64))
        {
            throw new InvalidOperationException(
                "Smart-X attachment encryption is not configured. " +
                "Set the Attachments__EncryptionKeyBase64 environment " +
                "variable to a Base64-encoded 32-byte key.");
        }

        try
        {
            _key = Convert.FromBase64String(
                encryptionKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The Smart-X attachment encryption key is not valid " +
                "Base64.",
                exception);
        }

        if (_key.Length != RequiredKeySizeBytes)
        {
            CryptographicOperations.ZeroMemory(_key);

            throw new InvalidOperationException(
                $"The Smart-X attachment encryption key must contain " +
                $"{RequiredKeySizeBytes} bytes after Base64 decoding.");
        }
    }

    public async Task<long> EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);

        if (!plaintext.CanRead)
        {
            throw new ArgumentException(
                "The plaintext stream must be readable.",
                nameof(plaintext));
        }

        if (!ciphertext.CanWrite)
        {
            throw new ArgumentException(
                "The ciphertext stream must be writable.",
                nameof(ciphertext));
        }

        var noncePrefix =
            RandomNumberGenerator.GetBytes(
                NoncePrefixSizeBytes);

        var header =
            BuildHeader(noncePrefix);

        await ciphertext.WriteAsync(
            header,
            cancellationToken);

        var plaintextBuffer =
            ArrayPool<byte>.Shared.Rent(
                ChunkSizeBytes);

        var ciphertextBuffer =
            ArrayPool<byte>.Shared.Rent(
                ChunkSizeBytes);

        var lengthBuffer =
            new byte[LengthFieldSizeBytes];

        var authenticationTag =
            new byte[AuthenticationTagSizeBytes];

        long totalPlaintextBytes = 0;

        uint chunkIndex = 0;

        using var aesGcm = new AesGcm(
            _key,
            AuthenticationTagSizeBytes);

        try
        {
            while (true)
            {
                var read = await plaintext.ReadAsync(
                    plaintextBuffer.AsMemory(
                        0,
                        ChunkSizeBytes),
                    cancellationToken);

                if (read == 0)
                {
                    break;
                }

                if (chunkIndex == uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The attachment contains too many encrypted " +
                        "chunks.");
                }

                var nonce =
                    BuildNonce(
                        noncePrefix,
                        chunkIndex);

                var associatedData =
                    BuildAssociatedData(
                        header,
                        chunkIndex,
                        read);

                aesGcm.Encrypt(
                    nonce,
                    plaintextBuffer.AsSpan(0, read),
                    ciphertextBuffer.AsSpan(0, read),
                    authenticationTag,
                    associatedData);

                BinaryPrimitives.WriteInt32LittleEndian(
                    lengthBuffer,
                    read);

                await ciphertext.WriteAsync(
                    lengthBuffer,
                    cancellationToken);

                await ciphertext.WriteAsync(
                    ciphertextBuffer.AsMemory(0, read),
                    cancellationToken);

                await ciphertext.WriteAsync(
                    authenticationTag,
                    cancellationToken);

                totalPlaintextBytes += read;

                chunkIndex++;
            }

            // A zero-length authenticated final record prevents an attacker
            // from truncating complete encrypted chunks from the end of a
            // file without detection.
            BinaryPrimitives.WriteInt32LittleEndian(
                lengthBuffer,
                0);

            var finalNonce =
                BuildNonce(
                    noncePrefix,
                    chunkIndex);

            var finalAssociatedData =
                BuildAssociatedData(
                    header,
                    chunkIndex,
                    0);

            aesGcm.Encrypt(
                finalNonce,
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty,
                authenticationTag,
                finalAssociatedData);

            await ciphertext.WriteAsync(
                lengthBuffer,
                cancellationToken);

            await ciphertext.WriteAsync(
                authenticationTag,
                cancellationToken);

            return totalPlaintextBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                plaintextBuffer);

            CryptographicOperations.ZeroMemory(
                ciphertextBuffer);

            ArrayPool<byte>.Shared.Return(
                plaintextBuffer);

            ArrayPool<byte>.Shared.Return(
                ciphertextBuffer);

            CryptographicOperations.ZeroMemory(
                authenticationTag);
        }
    }

    public async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!ciphertext.CanRead)
        {
            throw new ArgumentException(
                "The ciphertext stream must be readable.",
                nameof(ciphertext));
        }

        if (!plaintext.CanWrite)
        {
            throw new ArgumentException(
                "The plaintext stream must be writable.",
                nameof(plaintext));
        }

        var header =
            new byte[HeaderSizeBytes];

        await ciphertext.ReadExactlyAsync(
            header,
            cancellationToken);

        ValidateHeader(header);

        var noncePrefix =
            header.AsSpan(
                    Magic.Length,
                    NoncePrefixSizeBytes)
                .ToArray();

        var encryptedBuffer =
            ArrayPool<byte>.Shared.Rent(
                ChunkSizeBytes);

        var plaintextBuffer =
            ArrayPool<byte>.Shared.Rent(
                ChunkSizeBytes);

        var lengthBuffer =
            new byte[LengthFieldSizeBytes];

        var authenticationTag =
            new byte[AuthenticationTagSizeBytes];

        uint chunkIndex = 0;

        using var aesGcm = new AesGcm(
            _key,
            AuthenticationTagSizeBytes);

        try
        {
            while (true)
            {
                await ciphertext.ReadExactlyAsync(
                    lengthBuffer,
                    cancellationToken);

                var encryptedLength =
                    BinaryPrimitives.ReadInt32LittleEndian(
                        lengthBuffer);

                if (encryptedLength < 0 ||
                    encryptedLength > ChunkSizeBytes)
                {
                    throw new InvalidDataException(
                        "The encrypted attachment contains an invalid " +
                        "chunk length.");
                }

                if (chunkIndex == uint.MaxValue)
                {
                    throw new InvalidDataException(
                        "The encrypted attachment contains too many " +
                        "chunks.");
                }

                var nonce =
                    BuildNonce(
                        noncePrefix,
                        chunkIndex);

                var associatedData =
                    BuildAssociatedData(
                        header,
                        chunkIndex,
                        encryptedLength);

                if (encryptedLength == 0)
                {
                    await ciphertext.ReadExactlyAsync(
                        authenticationTag,
                        cancellationToken);

                    aesGcm.Decrypt(
                        nonce,
                        ReadOnlySpan<byte>.Empty,
                        authenticationTag,
                        Span<byte>.Empty,
                        associatedData);

                    var trailingByte =
                        new byte[1];

                    var trailingRead =
                        await ciphertext.ReadAsync(
                            trailingByte,
                            cancellationToken);

                    if (trailingRead != 0)
                    {
                        throw new InvalidDataException(
                            "The encrypted attachment contains " +
                            "unexpected trailing data.");
                    }

                    return;
                }

                await ciphertext.ReadExactlyAsync(
                    encryptedBuffer.AsMemory(
                        0,
                        encryptedLength),
                    cancellationToken);

                await ciphertext.ReadExactlyAsync(
                    authenticationTag,
                    cancellationToken);

                aesGcm.Decrypt(
                    nonce,
                    encryptedBuffer.AsSpan(
                        0,
                        encryptedLength),
                    authenticationTag,
                    plaintextBuffer.AsSpan(
                        0,
                        encryptedLength),
                    associatedData);

                await plaintext.WriteAsync(
                    plaintextBuffer.AsMemory(
                        0,
                        encryptedLength),
                    cancellationToken);

                chunkIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                encryptedBuffer);

            CryptographicOperations.ZeroMemory(
                plaintextBuffer);

            ArrayPool<byte>.Shared.Return(
                encryptedBuffer);

            ArrayPool<byte>.Shared.Return(
                plaintextBuffer);

            CryptographicOperations.ZeroMemory(
                authenticationTag);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    private static byte[] BuildHeader(
        byte[] noncePrefix)
    {
        var header =
            new byte[HeaderSizeBytes];

        Magic.CopyTo(
            header,
            0);

        noncePrefix.CopyTo(
            header,
            Magic.Length);

        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(
                Magic.Length + NoncePrefixSizeBytes,
                sizeof(int)),
            ChunkSizeBytes);

        return header;
    }

    private static void ValidateHeader(
        byte[] header)
    {
        if (!header
                .AsSpan(0, Magic.Length)
                .SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "The encrypted attachment has an unsupported format.");
        }

        var storedChunkSize =
            BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(
                    Magic.Length + NoncePrefixSizeBytes,
                    sizeof(int)));

        if (storedChunkSize != ChunkSizeBytes)
        {
            throw new InvalidDataException(
                "The encrypted attachment uses an unsupported chunk " +
                "size.");
        }
    }

    private static byte[] BuildNonce(
        byte[] noncePrefix,
        uint chunkIndex)
    {
        var nonce =
            new byte[NonceSizeBytes];

        noncePrefix.CopyTo(
            nonce,
            0);

        BinaryPrimitives.WriteUInt32LittleEndian(
            nonce.AsSpan(
                NoncePrefixSizeBytes,
                sizeof(uint)),
            chunkIndex);

        return nonce;
    }

    private static byte[] BuildAssociatedData(
        byte[] header,
        uint chunkIndex,
        int plaintextLength)
    {
        var associatedData =
            new byte[
                header.Length +
                sizeof(uint) +
                sizeof(int)];

        header.CopyTo(
            associatedData,
            0);

        BinaryPrimitives.WriteUInt32LittleEndian(
            associatedData.AsSpan(
                header.Length,
                sizeof(uint)),
            chunkIndex);

        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(
                header.Length + sizeof(uint),
                sizeof(int)),
            plaintextLength);

        return associatedData;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}