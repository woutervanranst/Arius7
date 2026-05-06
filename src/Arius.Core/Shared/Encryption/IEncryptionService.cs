using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.Encryption;

/// <summary>
/// Pluggable encryption and hashing service.
/// Two implementations: PassphraseEncryptionService (AES-256-CBC, openssl-compatible)
/// and PlaintextPassthroughService (no-op).
/// Selected at startup based on whether --passphrase is supplied.
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// Returns <c>true</c> when this service applies real encryption (AES-256-CBC);
    /// <c>false</c> for the plaintext passthrough.
    /// </summary>
    bool IsEncrypted { get; }

    /// <summary>
    /// Wraps <paramref name="inner"/> with an encryption layer.
    /// The returned stream is write-only; data written to it is encrypted and forwarded to <paramref name="inner"/>.
    /// Dispose the returned stream to flush and finalize the cipher.
    /// </summary>
    Stream WrapForEncryption(Stream inner);

    /// <summary>
    /// Wraps <paramref name="inner"/> with a decryption layer.
    /// The returned stream is read-only; bytes read from it are decrypted from <paramref name="inner"/>.
    /// </summary>
    Stream WrapForDecryption(Stream inner);

    /// <summary>
    /// Computes a content hash for a byte span.
    /// With a passphrase: SHA256(passphrase_bytes + data_bytes).
    /// Without a passphrase: SHA256(data_bytes).
    /// </summary>
    ContentHash ComputeHash(ReadOnlySpan<byte> data);

    /// <summary>
    /// Streaming variant of <see cref="ComputeHash(ReadOnlySpan{byte})"/>.
    /// Reads the entire stream to compute the hash; does not seek or reset the stream.
    /// </summary>
    Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default);

    /// <summary>
    /// File-path variant of <see cref="ComputeHashAsync(Stream, CancellationToken)"/>.
    /// Opens the file for reading and optionally reports cumulative bytes read.
    /// </summary>
    Task<ContentHash> ComputeHashAsync(
        RootedPath filePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
