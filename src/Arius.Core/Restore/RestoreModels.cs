using Arius.Core.Storage;
using Mediator;

namespace Arius.Core.Restore;

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
    public Func<RehydrationCostEstimate, CancellationToken, Task<RehydratePriority?>>? ConfirmRehydration { get; init; }

    /// <summary>
    /// Task 10.10: Optional callback invoked after a full restore when there are blobs to clean up.
    /// Return <c>true</c> to delete the rehydrated chunks, <c>false</c> to keep them.
    /// When <c>null</c>, blobs are retained.
    /// </summary>
    public Func<int, long, CancellationToken, Task<bool>>? ConfirmCleanup { get; init; }
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
/// Cost breakdown for a restore operation, emitted before rehydration begins.
/// </summary>
public sealed record RehydrationCostEstimate
{
    /// <summary>Chunks available for immediate download (Hot/Cool tier).</summary>
    public required int  ChunksAvailable        { get; init; }

    /// <summary>Chunks already in chunks-rehydrated/ (ready to download).</summary>
    public required int  ChunksAlreadyRehydrated { get; init; }

    /// <summary>Chunks in Archive tier that need rehydration.</summary>
    public required int  ChunksNeedingRehydration { get; init; }

    /// <summary>Chunks currently being rehydrated (pending from a previous run).</summary>
    public required int  ChunksPendingRehydration { get; init; }

    /// <summary>Total compressed bytes of chunks needing rehydration.</summary>
    public required long RehydrationBytes        { get; init; }

    /// <summary>Total compressed bytes available for immediate download.</summary>
    public required long DownloadBytes           { get; init; }

    /// <summary>
    /// Estimated rehydration cost (USD) at Standard priority (~$0.01/GB).
    /// </summary>
    public double EstimatedCostStandardUsd =>
        RehydrationBytes / (1024.0 * 1024.0 * 1024.0) * 0.01;

    /// <summary>
    /// Estimated rehydration cost (USD) at High priority (~$0.025/GB).
    /// </summary>
    public double EstimatedCostHighUsd =>
        RehydrationBytes / (1024.0 * 1024.0 * 1024.0) * 0.025;
}

// ── Task 10.12: Progress events ───────────────────────────────────────────────

/// <summary>Emitted when restore begins with file count.</summary>
public sealed record RestoreStartedEvent(int TotalFiles) : INotification;

/// <summary>Emitted when a single file has been restored to disk.</summary>
public sealed record FileRestoredEvent(string RelativePath) : INotification;

/// <summary>Emitted when a file was skipped (already present with matching hash).</summary>
public sealed record FileSkippedEvent(string RelativePath) : INotification;

/// <summary>Emitted when rehydration has been kicked off for some chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;

