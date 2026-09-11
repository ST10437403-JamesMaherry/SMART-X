namespace SmartX.Infrastructure.Attachments;

/// <summary>
/// Encrypts and decrypts attachment streams.
///
/// The abstraction exists inside Infrastructure because cryptographic
/// persistence is an implementation concern rather than an application
/// business rule.
/// </summary>
public interface IAttachmentCipher
{
    Task<long> EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        CancellationToken cancellationToken = default);

    Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        CancellationToken cancellationToken = default);
}