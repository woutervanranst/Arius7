using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.Restore;

// ── Task 10.1: Mediator RestoreCommand ────────────────────────────────────────

/// <summary>
/// Options controlling the restore pipeline.
/// </summary>
public sealed record RestoreOptions
{
    /// <summary>Local directory to restore files into.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>
    /// Snapshot version string (partial match). <c>null</c> = latest snapshot.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional path within the snapshot to restore.
    /// <c>null</c> or empty = full snapshot restore.
    /// </summary>
    public string? TargetPath { get; init; }

    /// <summary>If <c>true</c>, overwrite local files without prompting.</summary>
    public bool Overwrite { get; init; } = false;

    /// <summary>If <c>true</c>, do not create <c>.pointer.arius</c> files.</summary>
    public bool NoPointers { get; init; } = false;

    /// <summary>
    /// Task 10.6: Optional callback invoked with the cost estimate before rehydration begins.
    /// Return the desired <see cref="RehydratePriority"/> to proceed, or <c>null</c> to cancel.
    /// When this callback is <c>null</c>, all archive-tier chunks are rehydrated using Standard priority without confirmation.
    /// </summary>
    public Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>>? ConfirmRehydration { get; init; }

    /// <summary>
    /// Task 10.10: Optional callback invoked after a full restore when there are blobs to clean up.
    /// Return <c>true</c> to delete the rehydrated chunks, <c>false</c> to keep them.
    /// When <c>null</c>, blobs are retained.
    /// </summary>
    public Func<int, long, CancellationToken, Task<bool>>? ConfirmCleanup { get; init; }

    /// <summary>
    /// Optional factory that creates an <see cref="IProgress{T}"/> for tracking download bytes.
    /// Parameters: (identifier, compressedSize, kind). For large files, identifier is the file's RelativePath.
    /// For tar bundles, identifier is the chunk hash. When <c>null</c>, no download progress is reported.
    /// </summary>
    public Func<string, long, DownloadKind, IProgress<long>>? CreateDownloadProgress { get; init; }

}

/// <summary>
/// Mediator command: restore files from blob storage to a local directory.
/// </summary>
public sealed record RestoreCommand(RestoreOptions Options)
    : ICommand<RestoreResult>;

/// <summary>
/// Result returned by <see cref="RestoreCommand"/>.
/// </summary>
public sealed record RestoreResult
{
    public required bool   Success             { get; init; }
    public required int    FilesRestored       { get; init; }
    public required int    FilesSkipped        { get; init; }
    public required int    ChunksPendingRehydration { get; init; }
    public          string? ErrorMessage       { get; init; }
}

// ── Internal pipeline models ──────────────────────────────────────────────────

/// <summary>
/// A file entry collected during tree traversal that needs to be restored.
/// </summary>
internal sealed record FileToRestore(
    string         RelativePath,  // forward-slash, relative to archive root
    string         ContentHash,   // content hash (64-char hex)
    DateTimeOffset Created,
    DateTimeOffset Modified
);

/// <summary>
/// Rehydration availability status for a chunk.
/// </summary>
internal enum ChunkAvailability
{
    /// <summary>Available directly in chunks/ (Hot/Cool tier).</summary>
    Available,
    /// <summary>Already rehydrated in chunks-rehydrated/.</summary>
    Rehydrated,
    /// <summary>In Archive tier, needs rehydration copy.</summary>
    NeedsRehydration,
    /// <summary>Currently being rehydrated (copy pending).</summary>
    RehydrationPending,
}

/// <summary>
/// Represents a chunk with its availability status for restore.
/// </summary>
internal sealed record ChunkStatus(
    string           ChunkHash,
    ChunkAvailability Availability,
    long             CompressedSize
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

// ── Progress events ───────────────────────────────────────────────────────────

/// <summary>Emitted when restore begins with file count.</summary>
public sealed record RestoreStartedEvent(int TotalFiles) : INotification;

/// <summary>Emitted when a single file has been restored to disk.</summary>
public sealed record FileRestoredEvent(string RelativePath, long FileSize) : INotification;

/// <summary>Emitted when a file was skipped (already present with matching hash).</summary>
public sealed record FileSkippedEvent(string RelativePath, long FileSize) : INotification;

/// <summary>Emitted when rehydration has been kicked off for some chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;

/// <summary>Emitted after snapshot resolution and tree traversal gives the file count.</summary>
public sealed record SnapshotResolvedEvent(DateTimeOffset Timestamp, string RootHash, int FileCount) : INotification;

/// <summary>Emitted after all file entries are collected from the tree.</summary>
public sealed record TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize) : INotification;

/// <summary>Emitted periodically during tree traversal with the cumulative count of files discovered.</summary>
public sealed record TreeTraversalProgressEvent(int FilesFound) : INotification;

/// <summary>Disposition decision for each file during restore conflict check.</summary>
public enum RestoreDisposition { New, SkipIdentical, Overwrite, KeepLocalDiffers }

/// <summary>Emitted for each file's disposition decision during restore.</summary>
public sealed record FileDispositionEvent(string RelativePath, RestoreDisposition Disposition, long FileSize) : INotification;

/// <summary>Emitted after chunk index lookups complete.</summary>
public sealed record ChunkResolutionCompleteEvent(int ChunkGroups, int LargeCount, int TarCount, long TotalOriginalBytes = 0, long TotalCompressedBytes = 0) : INotification;

/// <summary>Emitted after rehydration availability check completes.</summary>
public sealed record RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending) : INotification;

/// <summary>Emitted when a chunk download begins.</summary>
public sealed record ChunkDownloadStartedEvent(string ChunkHash, string Type, int FileCount, long CompressedSize, long OriginalSize) : INotification;

/// <summary>Emitted after a tar bundle has been fully downloaded and extracted.</summary>
public sealed record ChunkDownloadCompletedEvent(string ChunkHash, int FilesRestored, long CompressedSize) : INotification;

/// <summary>Emitted after rehydrated blob cleanup finishes.</summary>
public sealed record CleanupCompleteEvent(int ChunksDeleted, long BytesFreed) : INotification;

