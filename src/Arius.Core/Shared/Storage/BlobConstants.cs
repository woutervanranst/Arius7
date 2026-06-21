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

    /// <summary>Compressed (encrypted+compressed) blob body size in bytes (for large and tar chunks).</summary>
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
    // NOTE: content types are informational only (the read path auto-detects gzip vs zstd from the
    // frame header). New blobs are written as zstd; "+gzip" variants remain for reading legacy blobs.

    // ── Chunk content types (GCM encrypted — new default, zstd) ─────────────────
    public const string LargeGcmEncrypted = "application/aes256gcm+zstd";
    public const string TarGcmEncrypted   = "application/aes256gcm+tar+zstd";

    // ── Chunk content types (CBC encrypted — legacy) ────────────────────────────
    public const string LargeCbcEncrypted = "application/aes256cbc+gzip";
    public const string TarCbcEncrypted   = "application/aes256cbc+tar+gzip";

    // ── Chunk content types (plaintext) ────────────────────────────────────────
    public const string LargePlaintext = "application/zstd";
    public const string TarPlaintext   = "application/tar+zstd";

    // ── Thin pointer ───────────────────────────────────────────────────────────
    public const string Thin           = "text/plain; charset=utf-8";

    // ── v5-legacy metadata sidecar (empty body; chunk descriptor carried in metadata) ──
    public const string V5LegacyMetadataSideCar = "text/plain; charset=utf-8";

    // ── File tree ──────────────────────────────────────────────────────────────
    public const string FileTreeGcmEncrypted = "application/aes256gcm+zstd";
    public const string FileTreeCbcEncrypted = "application/aes256cbc+gzip";
    public const string FileTreePlaintext    = "application/zstd";

    // ── Snapshot manifest ──────────────────────────────────────────────────────
    public const string SnapshotGcmEncrypted = "application/aes256gcm+zstd";
    public const string SnapshotCbcEncrypted = "application/aes256cbc+gzip";
    public const string SnapshotPlaintext    = "application/zstd";

    // ── Chunk index shard ──────────────────────────────────────────────────────
    public const string ChunkIndexGcmEncrypted = "application/aes256gcm+zstd";
    public const string ChunkIndexCbcEncrypted = "application/aes256cbc+gzip";
    public const string ChunkIndexPlaintext    = "application/zstd";
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

    /// <summary>
    /// Descriptor sidecars for chunks whose own metadata cannot be written — Archive-tier blobs migrated from v5, where Azure forbids Set Blob Metadata.
    /// NOTE: if chunk pruning/GC is ever added, deleting a chunk must also delete its descriptor sidecar.
    /// </summary>
    public static RelativePath V5LegacySideCarPrefix => RelativePath.Root / PathSegment.Parse("chunks-v5legacy-metadata");

    /// <summary>Merkle tree blobs (one per directory).</summary>
    public static RelativePath FileTreesPrefix => RelativePath.Root / PathSegment.Parse("filetrees");

    /// <summary>Snapshot manifests.</summary>
    public static RelativePath SnapshotsPrefix => RelativePath.Root / PathSegment.Parse("snapshots");

    /// <summary>Chunk index shards.</summary>
    public static RelativePath ChunkIndexPrefix => RelativePath.Root / PathSegment.Parse("chunk-index");

    public static RelativePath ChunkPath(ChunkHash hash)               => ChunksPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath ThinChunkPath(ContentHash hash)         => ChunksPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath V5LegacySideCarPath(ChunkHash hash)     => V5LegacySideCarPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath ChunkRehydratedPath(ChunkHash hash)     => ChunksRehydratedPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath FileTreePath(FileTreeHash hash)         => FileTreesPrefix / PathSegment.Parse(hash.ToString());
    public static RelativePath SnapshotPath(string name)               => SnapshotsPrefix / PathSegment.Parse(name);
    public static RelativePath SnapshotPath(DateTimeOffset timestamp)  => SnapshotPath(timestamp.UtcDateTime.ToString(Arius.Core.Shared.Snapshot.SnapshotService.TimestampFormat));
    public static RelativePath ChunkIndexShardPath(PathSegment prefix) => ChunkIndexPrefix / prefix;
}
