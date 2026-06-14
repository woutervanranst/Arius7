using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;

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
/// Mutable per-chunk restore plan built during classify and keyed by <see cref="ChunkHash"/>.
/// <see cref="RefCount"/> is the number of selected files referencing the chunk; download uses it to
/// close a tar group once all selected files for that chunk have arrived. Tar chunk sizes are summed
/// across selected files. A class is used because the handler mutates the accumulator in place.
/// </summary>
internal sealed class ChunkClassification
{
    public required bool                 IsLargeChunk   { get; init; }
    public required ChunkHydrationStatus Status         { get; set; }
    public          long                 CompressedSize { get; set; }
    public          long                 OriginalSize   { get; set; }
    public          int                  RefCount       { get; set; }
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
