using Arius.Core.FileTree;
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

// ── Task 10.12: Progress events ───────────────────────────────────────────────

/// <summary>Emitted when restore begins with file count.</summary>
public sealed record RestoreStartedEvent(int TotalFiles) : INotification;

/// <summary>Emitted when a single file has been restored to disk.</summary>
public sealed record FileRestoredEvent(string RelativePath) : INotification;

/// <summary>Emitted when a file was skipped (already present with matching hash).</summary>
public sealed record FileSkippedEvent(string RelativePath) : INotification;

/// <summary>Emitted when rehydration has been kicked off for some chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;
