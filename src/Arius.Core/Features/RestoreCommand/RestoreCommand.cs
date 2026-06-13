using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.RestoreCommand;

// ── COMMAND ────────────────────────────────────────

/// <summary>
/// Mediator command: restore files from blob storage to a local directory.
/// </summary>
public sealed record RestoreCommand(RestoreOptions Options) : ICommand<RestoreResult>;

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
    /// <c>null</c> = full snapshot restore.
    /// </summary>
    public RelativePath? TargetPath { get; init; }

    /// <summary>If <c>true</c>, overwrite local files without prompting.</summary>
    public bool Overwrite { get; init; } = false;

    /// <summary>If <c>true</c>, do not create <c>.pointer.arius</c> files.</summary>
    public bool NoPointers { get; init; } = false;

    /// <summary>
    /// Optional callback invoked with the cost estimate before rehydration begins.
    /// Return the desired <see cref="RehydratePriority"/> to proceed, or <c>null</c> to cancel.
    /// When this callback is <c>null</c>, all archive-tier chunks are rehydrated using Standard priority without confirmation.
    /// </summary>
    public Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>>? ConfirmRehydration { get; init; }

    /// <summary>
    /// Optional callback invoked after a full restore when there are blobs to clean up.
    /// Return <c>true</c> to delete the rehydrated chunks, <c>false</c> to keep them.
    /// When <c>null</c>, blobs are retained.
    /// </summary>
    public Func<int, long, CancellationToken, Task<bool>>? ConfirmCleanup { get; init; }

    /// <summary>
    /// Optional factory that creates an <see cref="IProgress{T}"/> for tracking large-file download bytes.
    /// Parameters: (relativePath, compressedSize). When <c>null</c>, no byte-level progress is reported for large files.
    /// </summary>
    public Func<RelativePath, long, IProgress<long>>? CreateLargeFileDownloadProgress { get; init; }

    /// <summary>
    /// Optional factory that creates an <see cref="IProgress{T}"/> for tracking tar-bundle download bytes.
    /// Parameters: (chunkHash, compressedSize). When <c>null</c>, no byte-level progress is reported for tar bundles.
    /// </summary>
    public Func<ChunkHash, long, IProgress<long>>? CreateTarBundleDownloadProgress { get; init; }

    /// <summary>
    /// Optional callback that hands the CLI a getter for the current download-queue depth (chunks resolved
    /// and waiting for a download worker). Mirrors archive's <c>OnHashQueueReady</c>/<c>OnUploadQueueReady</c>.
    /// </summary>
    public Action<Func<int>>? OnDownloadQueueReady { get; init; }
}


// ── RESULT ────────────────────────────────────────

/// <summary>
/// Result returned by <see cref="RestoreCommand"/>.
/// </summary>
public sealed record RestoreResult
{
    public required bool    Success                  { get; init; }
    public required int     FilesRestored            { get; init; }
    public required int     FilesSkipped             { get; init; }
    public required int     ChunksPendingRehydration { get; init; }
    public          string? ErrorMessage             { get; init; }
}
