using Mediator;

namespace Arius.Core.Features.RestoreCommand;

// ── Progress events ───────────────────────────────────────────────────────────

/// <summary>Emitted after classify has counted the selected files and restore work begins.</summary>
public sealed record RestoreStartedEvent(int TotalFiles) : INotification;

/// <summary>Emitted after one binary file has been written to disk.</summary>
public sealed record FileRestoredEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted when a selected file is not restored because the local copy is kept.</summary>
public sealed record FileSkippedEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted after restore requests rehydration for archive-tier chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;

/// <summary>Emitted after restore resolves the snapshot and counts selected files.</summary>
public sealed record SnapshotResolvedEvent(DateTimeOffset Timestamp, FileTreeHash RootHash, int FileCount) : INotification;

/// <summary>Emitted after the classify pass has walked all selected files.</summary>
public sealed record TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize) : INotification;

/// <summary>Emitted during the classify walk with the cumulative number of selected files discovered.</summary>
public sealed record TreeTraversalProgressEvent(int FilesFound) : INotification;

/// <summary>Local conflict decision made for a selected restore file.</summary>
public enum RestoreRoute { New, SkipIdentical, Overwrite, KeepLocalDiffers }

/// <summary>Emitted during classify when a selected file is routed.</summary>
public sealed record FileRoutedEvent(RelativePath RelativePath, RestoreRoute Route, long FileSize) : INotification;

/// <summary>Emitted after selected files have been grouped by resolved chunk.</summary>
public sealed record ChunkResolutionCompleteEvent(int ChunkGroups, int LargeCount, int TarCount, long TotalOriginalBytes = 0, long TotalCompressedBytes = 0) : INotification;

/// <summary>Emitted after classify determines chunk hydration status.</summary>
public sealed record RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending) : INotification;

/// <summary>Emitted before downloading one available chunk.</summary>
public sealed record ChunkDownloadStartedEvent(ChunkHash ChunkHash, string Type, int FileCount, long CompressedSize, long OriginalSize) : INotification;

/// <summary>Emitted after a tar chunk has been downloaded and all selected entries are restored.</summary>
public sealed record ChunkDownloadCompletedEvent(ChunkHash ChunkHash, int FilesRestored, long CompressedSize) : INotification;

/// <summary>Emitted after confirmed rehydrated chunk cleanup finishes.</summary>
public sealed record CleanupCompleteEvent(int ChunksDeleted, long BytesFreed) : INotification;
