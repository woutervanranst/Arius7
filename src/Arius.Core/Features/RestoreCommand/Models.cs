using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Features.RestoreCommand;

// ── Internal pipeline models ──────────────────────────────────────────────────

/// <summary>
/// A selected file paired with the chunk-index entry that locates its content.
/// </summary>
internal sealed record ResolvedFile(
    FileToRestore File,
    ShardEntry IndexEntry
);

/// <summary>
/// Snapshot file selected for restore before chunk-index resolution.
/// </summary>
internal sealed record FileToRestore(
    RelativePath   RelativePath,
    ContentHash    ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified
);

/// <summary>
/// In-progress tar chunk group built during download pass #2 and flushed after the walk completes.
/// </summary>
internal sealed class OpenTarChunk
{
    public List<FileToRestore> Files { get; } = [];

    public long CompressedSize { get; set; }

    public long OriginalSize { get; set; }
}

/// <summary>
/// A closed chunk group dispatched to a download worker. Large chunks contain one file; tar chunks contain
/// every selected file mapped to the chunk.
/// </summary>
internal sealed record ChunkToRestore(
    ChunkHash                   ChunkHash,
    bool                        IsLargeChunk,
    IReadOnlyList<FileToRestore> Files,
    long                        CompressedSize,
    long                        OriginalSize
);

// ── Download kind enum ────────────────────────────────────────────────────────

/// <summary>Download shape used for restore progress display.</summary>
public enum DownloadKind
{
    /// <summary>A large chunk containing one file.</summary>
    LargeFile,

    /// <summary>A tar chunk containing one or more small files.</summary>
    TarBundle,
}
