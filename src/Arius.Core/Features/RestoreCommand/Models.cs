using Arius.Core.Shared.Hashes;

namespace Arius.Core.Features.RestoreCommand;

// ── Internal pipeline models ──────────────────────────────────────────────────

/// <summary>
/// A file entry collected during tree traversal that needs to be restored.
/// </summary>
internal sealed record FileToRestore(
    string         RelativePath,  // forward-slash, relative to archive root
    ContentHash    ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified
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
