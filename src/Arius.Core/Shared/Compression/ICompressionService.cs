namespace Arius.Core.Shared.Compression;

/// <summary>
/// Pluggable compression for blob bodies, mirroring the stream-wrapping shape of
/// <see cref="Arius.Core.Shared.Encryption.IEncryptionService"/>.
///
/// New blobs are always written as zstd (RFC 8878). The read path is self-describing:
/// it auto-detects the algorithm from the leading magic bytes, so it transparently
/// decompresses both new zstd blobs and legacy gzip blobs without relying on any
/// external metadata or content-type.
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// Wraps <paramref name="destination"/> with a compression layer (zstd).
    /// The returned stream is write-only; data written to it is compressed and forwarded to
    /// <paramref name="destination"/>. Dispose the returned stream to finalize the frame.
    /// </summary>
    Stream WrapForCompression(Stream destination, bool leaveOpen = true);

    /// <summary>
    /// Wraps <paramref name="source"/> with a decompression layer, auto-detecting zstd vs gzip
    /// from the leading magic bytes. The returned stream is read-only.
    /// </summary>
    Stream WrapForDecompression(Stream source, bool leaveOpen = false);
}
