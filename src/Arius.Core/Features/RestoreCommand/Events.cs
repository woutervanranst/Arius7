using Mediator;

namespace Arius.Core.Features.RestoreCommand;

// ── Progress events ───────────────────────────────────────────────────────────

/// <summary>Emitted after one binary file has been written to disk.</summary>
public sealed record FileRestoredEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted when a selected file is not restored because the local copy is kept.</summary>
public sealed record FileSkippedEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>Emitted after restore requests rehydration for archive-tier chunks.</summary>
public sealed record RehydrationStartedEvent(int ChunkCount, long TotalBytes) : INotification;

/// <summary>Emitted after restore resolves the snapshot.</summary>
public sealed record SnapshotResolvedEvent(DateTimeOffset Timestamp, FileTreeHash RootHash) : INotification;

/// <summary>Emitted after the classify pass has walked all selected files.</summary>
public sealed record TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize) : INotification;

/// <summary>Emitted during the classify walk with the cumulative number of selected files discovered.</summary>
public sealed record TreeTraversalProgressEvent(int FilesFound) : INotification;

/// <summary>Local conflict decision made for a selected restore file.</summary>
public enum RestoreRoute { New, SkipIdentical, Overwrite, KeepLocalDiffers }

/// <summary>Emitted during classify when a selected file is routed.</summary>
public sealed record FileRoutedEvent(RelativePath RelativePath, RestoreRoute Route, long FileSize) : INotification;

/// <summary>Emitted after classify has counted the distinct chunks needed by selected files.</summary>
public sealed record ChunkResolutionCompleteEvent(int TotalChunks, int LargeCount, int TarCount, long TotalChunkBytes = 0) : INotification;

/// <summary>Emitted after classify determines chunk hydration status.</summary>
public sealed record RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending) : INotification;

/// <summary>Emitted before downloading one available chunk.</summary>
public sealed record ChunkDownloadStartedEvent(ChunkHash ChunkHash, string Type, int FileCount, long ChunkSize, long OriginalSize) : INotification;

/// <summary>Emitted after a tar chunk has been downloaded and all selected entries are restored.</summary>
public sealed record ChunkDownloadCompletedEvent(ChunkHash ChunkHash, int FilesRestored, long ChunkSize) : INotification;

/// <summary>Emitted after confirmed rehydrated chunk cleanup finishes.</summary>
public sealed record CleanupCompleteEvent(int ChunksDeleted) : INotification;
