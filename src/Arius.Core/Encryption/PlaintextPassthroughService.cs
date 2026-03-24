using System.Security.Cryptography;

namespace Arius.Core.Encryption;

/// <summary>
/// No-op encryption service for plaintext (unencrypted) repositories.
/// WrapForEncryption / WrapForDecryption return the stream unchanged.
/// Hash is plain SHA256(data_bytes).
/// </summary>
public sealed class PlaintextPassthroughService : IEncryptionService
{
    /// <inheritdoc/>
    public bool IsEncrypted => false;

    /// <inheritdoc/>
    /// Returns <paramref name="inner"/> unchanged — no encryption applied.
    public Stream WrapForEncryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner;
    }

    /// <inheritdoc/>
    /// Returns <paramref name="inner"/> unchanged — no decryption applied.
    public Stream WrapForDecryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner;
    }

    /// <inheritdoc/>
    public byte[] ComputeHash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    /// <inheritdoc/>
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return await SHA256.HashDataAsync(data, cancellationToken);
    }
}
