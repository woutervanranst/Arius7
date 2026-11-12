using Arius.Core.Shared.Compression;

namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

internal sealed class EncodeOnlyCompressionService : ICompressionService
{
    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
        => new ZstdCompressionService().WrapForCompression(destination, leaveOpen);

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => throw new InvalidOperationException("Uploading must not decode the compressed output.");
}
