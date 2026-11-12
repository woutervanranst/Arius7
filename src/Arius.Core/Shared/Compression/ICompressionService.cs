namespace Arius.Core.Shared.Compression;

/// <summary>
/// Stream-based compression for stored payloads. Production readers detect Zstandard or legacy gzip
/// from frame headers without external metadata.
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// Dispose the returned write-only stream to finalize the compression frame.
    /// </summary>
    Stream WrapForCompression(Stream destination, bool leaveOpen = true);

    Stream WrapForDecompression(Stream source, bool leaveOpen = false);
}
