using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

internal sealed class EncryptedPassthroughService : IEncryptionService
{
    public bool IsEncrypted => true;

    public Stream WrapForEncryption(Stream inner) => inner;

    public Stream WrapForDecryption(Stream inner) => inner;

    public ContentHash ComputeHash(byte[] data) => throw new NotSupportedException();

    public Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ContentHash> ComputeHashAsync(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
