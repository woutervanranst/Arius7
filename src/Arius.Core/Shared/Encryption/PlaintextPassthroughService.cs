using System.Security.Cryptography;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Streaming;

namespace Arius.Core.Shared.Encryption;

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
    public ContentHash ComputeHash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ContentHash.FromDigest(SHA256.HashData(data));
    }

    /// <inheritdoc/>
    public async Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ContentHash.FromDigest(await SHA256.HashDataAsync(data, cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<ContentHash> ComputeHashAsync(
        string filePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        await using var file = File.OpenRead(filePath);
        if (progress is null)
            return await ComputeHashAsync(file, cancellationToken);

        await using var progressStream = new ProgressStream(file, progress);
        return await ComputeHashAsync(progressStream, cancellationToken);
    }
}
