namespace Arius.Core.Storage;

/// <summary>
/// Well-known blob metadata key names used across the system.
/// These are stored as Azure Blob metadata (lowercase, no special chars).
/// </summary>
public static class BlobMetadataKeys
{
    /// <summary>Chunk type: "large", "tar", or "thin".</summary>
    public const string AriusType          = "arius_type";

    /// <summary>Original uncompressed file size in bytes (for large and thin chunks).</summary>
    public const string OriginalSize       = "original_size";

    /// <summary>Compressed (encrypted+gzipped) blob body size in bytes (for large and tar chunks).</summary>
    public const string ChunkSize         = "chunk_size";

    /// <summary>Proportional compressed size estimate for this file within a tar bundle (for thin chunks).</summary>
    public const string CompressedSize    = "compressed_size";

    // ── Chunk type values ──────────────────────────────────────────────────────

    public const string TypeLarge = "large";
    public const string TypeTar   = "tar";
    public const string TypeThin  = "thin";
}

/// <summary>
/// Well-known content types for blobs uploaded by Arius.
/// Allows quick identification of blob content without inspecting metadata.
/// </summary>
public static class ContentTypes
{
    // ── Chunk content types (GCM encrypted — new default) ──────────────────────
    public const string LargeGcmEncrypted = "application/aes256gcm+gzip";
    public const string TarGcmEncrypted   = "application/aes256gcm+tar+gzip";

    // ── Chunk content types (CBC encrypted — legacy) ────────────────────────────
    public const string LargeCbcEncrypted = "application/aes256cbc+gzip";
    public const string TarCbcEncrypted   = "application/aes256cbc+tar+gzip";

    // ── Chunk content types (plaintext) ────────────────────────────────────────
    public const string LargePlaintext = "application/gzip";
    public const string TarPlaintext   = "application/tar+gzip";

    // ── Thin pointer ───────────────────────────────────────────────────────────
    public const string Thin           = "text/plain; charset=utf-8";

    // ── File tree ──────────────────────────────────────────────────────────────
    public const string FileTreeGcmEncrypted = "application/aes256gcm+gzip";
    public const string FileTreeCbcEncrypted = "application/aes256cbc+gzip";
    public const string FileTreePlaintext    = "application/gzip";

    // ── Snapshot manifest ──────────────────────────────────────────────────────
    public const string SnapshotGcmEncrypted = "application/aes256gcm+gzip";
    public const string SnapshotCbcEncrypted = "application/aes256cbc+gzip";
    public const string SnapshotPlaintext    = "application/gzip";

    // ── Chunk index shard ──────────────────────────────────────────────────────
    public const string ChunkIndexGcmEncrypted = "application/aes256gcm+gzip";
    public const string ChunkIndexCbcEncrypted = "application/aes256cbc+gzip";
    public const string ChunkIndexPlaintext    = "application/gzip";
}

/// <summary>
/// Virtual directory prefixes within the blob container.
/// </summary>
public static class BlobPaths
{
    /// <summary>Content-addressable chunks: large files and thin pointers.</summary>
    public const string Chunks            = "chunks/";

    /// <summary>Temporary Hot-tier copies for in-progress rehydration.</summary>
    public const string ChunksRehydrated  = "chunks-rehydrated/";

    /// <summary>Merkle tree blobs (one per directory).</summary>
    public const string FileTrees         = "filetrees/";

    /// <summary>Snapshot manifests.</summary>
    public const string Snapshots         = "snapshots/";

    /// <summary>Chunk index shards (65536 shards by 2-byte prefix).</summary>
    public const string ChunkIndex        = "chunk-index/";

    public static string Chunk(string hash)           => $"{Chunks}{hash}";
    public static string ChunkRehydrated(string hash) => $"{ChunksRehydrated}{hash}";
    public static string FileTree(string hash)        => $"{FileTrees}{hash}";
    public static string Snapshot(string name)        => $"{Snapshots}{name}";
    public static string ChunkIndexShard(string prefix) => $"{ChunkIndex}{prefix}";
}
