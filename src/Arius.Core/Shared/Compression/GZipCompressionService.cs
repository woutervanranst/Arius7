using System.IO.Compression;

namespace Arius.Core.Shared.Compression;

/// <summary>
/// Legacy gzip <see cref="ICompressionService"/> backed by the BCL <see cref="GZipStream"/>. Arius no longer
/// writes gzip — every new blob is zstd — but old "+gzip" blobs must stay readable forever, so this owns the
/// gzip codec that <see cref="ZstdCompressionService"/>'s self-describing read path delegates to whenever it
/// detects a legacy frame header.
/// </summary>
[SharedWithinAssembly]
internal sealed class GZipCompressionService : ICompressionService
{
    // gzip frame magic: 0x1F 0x8B (little-endian on disk).
    private const byte Magic0 = 0x1F;
    private const byte Magic1 = 0x8B;

    /// <summary>True when <paramref name="header"/> begins with the gzip frame magic.</summary>
    public static bool IsGZipHeader(ReadOnlySpan<byte> header)
        => header.Length >= 2 && header[0] == Magic0 && header[1] == Magic1;

    /// <summary>
    /// gzip never needs inline round-trip verification: the BCL gzip/deflate encoder is mature and trusted
    /// (and Arius does not write new gzip blobs anyway), unlike the newer zstd encoder.
    /// </summary>
    public bool RequireRoundTripVerification => false;

    /// <summary>
    /// The legacy writer, kept faithful to what the gzip era of Arius produced (<see cref="CompressionLevel.SmallestSize"/>).
    /// Unused on the production write path — new blobs are zstd — but it keeps this a complete, testable codec.
    /// </summary>
    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
        => new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen);

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => new GZipStream(source, CompressionMode.Decompress, leaveOpen);
}
