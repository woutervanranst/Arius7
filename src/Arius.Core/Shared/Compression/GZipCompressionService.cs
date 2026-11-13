using System.IO.Compression;

namespace Arius.Core.Shared.Compression;

/// <summary>
/// Decodes legacy gzip payloads for <see cref="ZstdCompressionService"/>.
/// </summary>
[SharedWithinAssembly]
internal sealed class GZipCompressionService : ICompressionService
{
    // gzip frame magic: 0x1F 0x8B (little-endian on disk).
    private const byte Magic0 = 0x1F;
    private const byte Magic1 = 0x8B;

    public static bool IsGZipHeader(ReadOnlySpan<byte> header)
        => header.Length >= 2 && header[0] == Magic0 && header[1] == Magic1;

    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
        => new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen);

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => new GZipStream(source, CompressionMode.Decompress, leaveOpen);
}
