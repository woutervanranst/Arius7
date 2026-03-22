namespace Arius.Core.Storage;

/// <summary>
/// Well-known blob metadata key names used across the system.
/// These are stored as Azure Blob metadata (lowercase, no special chars).
/// </summary>
public static class BlobMetadataKeys
{
    /// <summary>Chunk type: "large", "tar", or "thin".</summary>
    public const string AriusType          = "arius-type";

    /// <summary>Set to "true" once an upload completes successfully. Absence means incomplete.</summary>
    public const string AriusComplete      = "arius-complete";

    /// <summary>Original uncompressed file size in bytes (for large and thin chunks).</summary>
    public const string OriginalSize       = "original-size";

    /// <summary>Compressed (encrypted+gzipped) blob body size in bytes (for large and tar chunks).</summary>
    public const string ChunkSize         = "chunk-size";

    /// <summary>Proportional compressed size estimate for this file within a tar bundle (for thin chunks).</summary>
    public const string CompressedSize    = "compressed-size";

    // ── Chunk type values ──────────────────────────────────────────────────────

    public const string TypeLarge = "large";
    public const string TypeTar   = "tar";
    public const string TypeThin  = "thin";
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
