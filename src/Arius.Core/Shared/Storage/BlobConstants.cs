namespace Arius.Core.Shared.Storage;

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

    /// <summary>Parent tar chunk hash for a thin chunk.</summary>
    public const string ParentChunkHash   = "parent_chunk_hash";

    // ── Chunk type values ──────────────────────────────────────────────────────

    public const string TypeLarge = "large";
    public const string TypeTar   = "tar";
    public const string TypeThin  = "thin";
}

/// <summary>
/// Well-known content types for blobs uploaded by Arius.
/// Allows quick identification of blob content without inspecting metadata.
/// </summary>
[SharedWithinAssembly]
internal static class ContentTypes
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
    // NOTE: These methods require a strong domain type (unless there is none). For string convenience overloads used in Test suites, see Arius.Tests.Shared.BlobPathsExtensions.

    /// <summary>Content-addressable chunks: large files and thin pointers.</summary>
    public static RelativePath ChunksPrefix => RelativePath.Root / PathSegment.Parse("chunks");

    /// <summary>Temporary Hot-tier copies for in-progress rehydration.</summary>
    public static RelativePath ChunksRehydratedPrefix => RelativePath.Root / PathSegment.Parse("chunks-rehydrated");

    /// <summary>Merkle tree blobs (one per directory).</summary>
    public static RelativePath FileTreesPrefix => RelativePath.Root / PathSegment.Parse("filetrees");

    /// <summary>Snapshot manifests.</summary>
    public static RelativePath SnapshotsPrefix => RelativePath.Root / PathSegment.Parse("snapshots");

    /// <summary>Chunk index shards.</summary>
    public static RelativePath ChunkIndexPrefix => RelativePath.Root / PathSegment.Parse("chunk-index");

    public static RelativePath ChunkPath(ChunkHash hash)               => ChunksPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath ThinChunkPath(ContentHash hash)         => ChunksPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath ChunkRehydratedPath(ChunkHash hash)     => ChunksRehydratedPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath FileTreePath(FileTreeHash hash)         => FileTreesPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath SnapshotPath(string name)               => SnapshotsPrefix / PathSegment.Parse(name);
    public static RelativePath SnapshotPath(DateTimeOffset timestamp)  => SnapshotPath(timestamp.UtcDateTime.ToString(Arius.Core.Shared.Snapshot.SnapshotService.TimestampFormat));
    public static RelativePath ChunkIndexShardPath(PathSegment prefix) => ChunkIndexPrefix / prefix;
}
