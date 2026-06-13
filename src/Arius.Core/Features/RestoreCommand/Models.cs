using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;

namespace Arius.Core.Features.RestoreCommand;

// ── Internal pipeline models ──────────────────────────────────────────────────

/// <summary>
/// A file entry collected during tree traversal that needs to be restored.
/// </summary>
internal sealed record FileToRestore(
    RelativePath   RelativePath,
    ContentHash    ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified
);

/// <summary>
/// A <see cref="FileToRestore"/> paired with its resolved chunk-index entry (the Resolve stage's output).
/// Carries the index entry so downstream stages need not re-look-up the chunk.
/// </summary>
internal sealed record ResolvedFile(
    FileToRestore File,
    ShardEntry    IndexEntry
);

/// <summary>
/// Mutable per-chunk accumulator built during classify (keyed by distinct <see cref="ChunkHash"/>).
/// <see cref="RefCount"/> is the number of to-restore files referencing the chunk; download uses it to
/// flush a tar group once all its files have arrived. For tar chunks the sizes are summed across files.
/// A class (not a record) because it is mutated in place through the classification map.
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
/// A closed group dispatched to a pass-2 download worker. For large chunks <see cref="Files"/> holds the
/// single file; for tar bundles it holds every to-restore file mapped to the chunk.
/// </summary>
internal sealed record ChunkToRestore(
    ChunkHash                   ChunkHash,
    bool                        IsLargeChunk,
    IReadOnlyList<FileToRestore> Files,
    long                        CompressedSize,
    long                        OriginalSize
);

// ── Task 10.6: Cost estimation model ─────────────────────────────────────────

/// <summary>
/// Full cost breakdown for a restore operation, emitted before rehydration begins.
/// All monetary values are in the currency configured in <c>pricing.json</c> (default: EUR).
/// </summary>
public sealed record RestoreCostEstimate
{
    // ── Chunk availability counts ─────────────────────────────────────────────

    /// <summary>Chunks available for immediate download (Hot/Cool tier).</summary>
    public required int  ChunksAvailable          { get; init; }

    /// <summary>Chunks already in chunks-rehydrated/ (ready to download).</summary>
    public required int  ChunksAlreadyRehydrated   { get; init; }

    /// <summary>Chunks in Archive tier that need rehydration.</summary>
    public required int  ChunksNeedingRehydration  { get; init; }

    /// <summary>Chunks currently being rehydrated (pending from a previous run).</summary>
    public required int  ChunksPendingRehydration  { get; init; }

    /// <summary>Total compressed bytes of chunks needing rehydration.</summary>
    public required long RehydrationBytes          { get; init; }

    /// <summary>Total compressed bytes available for immediate download.</summary>
    public required long DownloadBytes             { get; init; }

    // ── Per-component cost fields ─────────────────────────────────────────────

    /// <summary>Data retrieval cost at Standard priority (archive → rehydrated).</summary>
    public required double RetrievalCostStandard   { get; init; }

    /// <summary>Data retrieval cost at High priority.</summary>
    public required double RetrievalCostHigh       { get; init; }

    /// <summary>Read operations cost on archive blobs at Standard priority.</summary>
    public required double ReadOpsCostStandard     { get; init; }

    /// <summary>Read operations cost on archive blobs at High priority.</summary>
    public required double ReadOpsCostHigh         { get; init; }

    /// <summary>Write operations cost to the target (Hot) tier.</summary>
    public required double WriteOpsCost            { get; init; }

    /// <summary>Storage cost for rehydrated copies (default: 1 month, Hot tier).</summary>
    public required double StorageCost             { get; init; }

    // ── Computed totals ───────────────────────────────────────────────────────

    /// <summary>Total estimated cost at Standard priority.</summary>
    public double TotalStandard =>
        RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost;

    /// <summary>Total estimated cost at High priority.</summary>
    public double TotalHigh =>
        RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost;
}

// ── Download kind enum ────────────────────────────────────────────────────────

/// <summary>Discriminates between large-file and tar-bundle downloads for progress display.</summary>
public enum DownloadKind
{
    /// <summary>A single large file mapped 1:1 to a chunk.</summary>
    LargeFile,

    /// <summary>A tar bundle containing multiple small files.</summary>
    TarBundle,
}
