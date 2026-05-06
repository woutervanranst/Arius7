using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Integration.Tests.Pipeline.Fakes;

/// <summary>
/// Wraps <see cref="PassphraseEncryptionService"/> so that <see cref="WrapForEncryption"/>
/// produces CBC (legacy Salted__ format) instead of GCM output. Used to simulate a
/// pre-existing legacy archive run in backwards-compatibility tests.
/// All other members delegate unchanged so the chunk index and restore path work correctly.
/// </summary>
internal sealed class CbcEncryptionServiceAdapter(string passphrase) : IEncryptionService
{
    private readonly PassphraseEncryptionService _inner = new(passphrase);

    public bool IsEncrypted => _inner.IsEncrypted;

    /// <summary>Writes CBC (legacy Salted__ format) instead of GCM.</summary>
    public Stream WrapForEncryption(Stream inner) => _inner.WrapForCbcEncryption(inner);

    /// <summary>Auto-detects magic bytes — handles both CBC and GCM on read.</summary>
    public Stream WrapForDecryption(Stream inner) => _inner.WrapForDecryption(inner);

    public ContentHash ComputeHash(ReadOnlySpan<byte> data) => _inner.ComputeHash(data);

    public Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken ct = default) =>
        _inner.ComputeHashAsync(data, ct);

    public Task<ContentHash> ComputeHashAsync(RootedPath filePath, IProgress<long>? progress = null, CancellationToken ct = default) =>
        _inner.ComputeHashAsync(filePath, progress, ct);
}
