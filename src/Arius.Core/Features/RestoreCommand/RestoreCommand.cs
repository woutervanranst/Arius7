using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.RestoreCommand;

// ── COMMAND ────────────────────────────────────────

/// <summary>
/// Mediator command that restores files from a repository snapshot to a local directory.
/// </summary>
public sealed record RestoreCommand(RestoreOptions Options) : ICommand<RestoreResult>;

/// <summary>
/// User-facing restore settings that select the snapshot, target path, local conflict behavior, and progress callbacks.
/// </summary>
public sealed record RestoreOptions
{
    /// <summary>Local directory where restored files are written.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>
    /// Snapshot version to restore. A partial match is accepted; <c>null</c> restores the latest snapshot.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional repository-relative path within the snapshot to restore.
    /// <c>null</c> restores the full snapshot.
    /// </summary>
    public RelativePath? TargetPath { get; init; }

    /// <summary>If <c>true</c>, replace existing local files instead of skipping conflicting files.</summary>
    public bool Overwrite { get; init; } = false;

    /// <summary>If <c>true</c>, restore binary files without writing companion pointer files.</summary>
    public bool NoPointers { get; init; } = false;

    /// <summary>
    /// Optional callback invoked with the cost estimate before downloads or rehydration begin.
    /// Return the desired <see cref="RehydratePriority"/> to proceed, or <c>null</c> to cancel before files are written.
    /// When this callback is <c>null</c>, archive-tier chunks are rehydrated using Standard priority without confirmation.
    /// </summary>
    public Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>>? ConfirmRehydration { get; init; }

    /// <summary>
    /// Optional callback invoked after restore when there are rehydrated chunk blobs that can be cleaned up.
    /// Return <c>true</c> to delete them, <c>false</c> to keep them. When <c>null</c>, they are retained.
    /// </summary>
    public Func<int, long, CancellationToken, Task<bool>>? ConfirmCleanup { get; init; }

    /// <summary>
    /// Optional factory that creates an <see cref="IProgress{T}"/> for a large-chunk download.
    /// Parameters: (relativePath, chunkSize). When <c>null</c>, byte-level progress is not reported.
    /// </summary>
    public Func<RelativePath, long, IProgress<long>>? CreateLargeFileDownloadProgress { get; init; }

    /// <summary>
    /// Optional factory that creates an <see cref="IProgress{T}"/> for a tar-chunk download.
    /// Parameters: (chunkHash, chunkSize). When <c>null</c>, byte-level progress is not reported.
    /// </summary>
    public Func<ChunkHash, long, IProgress<long>>? CreateTarBundleDownloadProgress { get; init; }

    /// <summary>
    /// Optional callback that hands the CLI a getter for the current download-queue depth: chunks resolved
    /// and waiting for a download worker.
    /// </summary>
    public Action<Func<int>>? OnDownloadQueueReady { get; init; }
}


// ── RESULT ────────────────────────────────────────

/// <summary>
/// Outcome of a restore command.
/// </summary>
public sealed record RestoreResult
{
    public required bool    Success                  { get; init; }
    public required int     FilesRestored            { get; init; }
    public required int     FilesSkipped             { get; init; }
    public required int     ChunksPendingRehydration { get; init; }
    public          string? ErrorMessage             { get; init; }
}
