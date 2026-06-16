namespace Arius.Core.Shared.Compression;

/// <summary>
/// Pluggable compression for blob bodies, mirroring the stream-wrapping shape of
/// <see cref="Arius.Core.Shared.Encryption.IEncryptionService"/>.
///
/// New blobs are always written as zstd (RFC 8878). The read path is self-describing:
/// it auto-detects the algorithm from the leading magic bytes, so it transparently
/// decompresses both new zstd blobs and legacy gzip blobs without relying on any
/// external metadata or content-type. See <see cref="ZstdCompressionService"/> (current codec)
/// and <see cref="GZipCompressionService"/> (legacy codec, read-only in production).
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// Whether chunk uploads must verify this codec round-trips inline (compress → decompress → re-hash)
    /// before recording the chunk. <c>true</c> for zstd — the newer encoder is verified at archive time so
    /// an encoder bug fails loudly while the source is still on disk; <c>false</c> for the legacy gzip path,
    /// whose mature BCL encoder we trust. <see cref="Arius.Core.Shared.ChunkStorage.ChunkStorageService"/>
    /// reads this to gate verification.
    /// </summary>
    bool RequireRoundTripVerification { get; }

    /// <summary>
    /// Wraps <paramref name="destination"/> with a compression layer.
    /// The returned stream is write-only; data written to it is compressed and forwarded to
    /// <paramref name="destination"/>. Dispose the returned stream to finalize the frame.
    /// </summary>
    Stream WrapForCompression(Stream destination, bool leaveOpen = true);

    /// <summary>
    /// Wraps <paramref name="source"/> with a decompression layer. The returned stream is read-only.
    /// </summary>
    Stream WrapForDecompression(Stream source, bool leaveOpen = false);
}
