namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

internal sealed class EncryptOnlyEncryptionService(IEncryptionService inner) : IEncryptionService
{
    public bool IsEncrypted => inner.IsEncrypted;

    public Stream WrapForEncryption(Stream stream) => inner.WrapForEncryption(stream);

    public Stream WrapForDecryption(Stream stream)
        => throw new InvalidOperationException("Uploading must not decrypt its output.");

    public ContentHash ComputeHash(ReadOnlySpan<byte> data)
        => throw new InvalidOperationException("Uploading must not rehash the source.");

    public Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Uploading must not rehash the source.");
}
