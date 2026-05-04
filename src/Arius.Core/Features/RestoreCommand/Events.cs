using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Mediator;

namespace Arius.Core.Features.RestoreCommand;

// ── Progress events ───────────────────────────────────────────────────────────

/// <summary>Emitted when restore begins with file count.</summary>
public sealed record RestoreStartedEvent(int TotalFiles) : INotification;

/// <summary>Emitted when a single file has been restored to disk.</summary>
public sealed record FileRestoredEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted when a file was skipped (already present with matching hash).</summary>
public sealed record FileSkippedEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted when rehydration has been kicked off for some chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;

/// <summary>Emitted after snapshot resolution and tree traversal gives the file count.</summary>
public sealed record SnapshotResolvedEvent(DateTimeOffset Timestamp, FileTreeHash RootHash, int FileCount) : INotification;

/// <summary>Emitted after all file entries are collected from the tree.</summary>
public sealed record TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize) : INotification;

/// <summary>Emitted periodically during tree traversal with the cumulative count of files discovered.</summary>
public sealed record TreeTraversalProgressEvent(int FilesFound) : INotification;

/// <summary>Disposition decision for each file during restore conflict check.</summary>
public enum RestoreDisposition { New, SkipIdentical, Overwrite, KeepLocalDiffers }

/// <summary>Emitted for each file's disposition decision during restore.</summary>
public sealed record FileDispositionEvent(RelativePath RelativePath, RestoreDisposition Disposition, long FileSize) : INotification;

/// <summary>Emitted after chunk index lookups complete.</summary>
public sealed record ChunkResolutionCompleteEvent(int ChunkGroups, int LargeCount, int TarCount, long TotalOriginalBytes = 0, long TotalCompressedBytes = 0) : INotification;

/// <summary>Emitted after rehydration availability check completes.</summary>
public sealed record RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending) : INotification;

/// <summary>Emitted when a chunk download begins.</summary>
public sealed record ChunkDownloadStartedEvent(ChunkHash ChunkHash, string Type, int FileCount, long CompressedSize, long OriginalSize) : INotification;

/// <summary>Emitted after a tar bundle has been fully downloaded and extracted.</summary>
public sealed record ChunkDownloadCompletedEvent(ChunkHash ChunkHash, int FilesRestored, long CompressedSize) : INotification;

/// <summary>Emitted after rehydrated blob cleanup finishes.</summary>
public sealed record CleanupCompleteEvent(int ChunksDeleted, long BytesFreed) : INotification;
