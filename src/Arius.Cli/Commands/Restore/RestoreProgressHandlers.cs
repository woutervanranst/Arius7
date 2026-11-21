using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Commands.Restore;

// ── FileRestoredHandler ───────────────────────────────────────────────────────

/// <summary>
/// Increments <see cref="ProgressState.FilesRestored"/> and <see cref="ProgressState.BytesRestored"/> when a file is written to disk.
/// For large-file downloads, also removes the corresponding <see cref="TrackedDownload"/>.
/// </summary>
internal sealed class FileRestoredHandler(ProgressState state) : INotificationHandler<FileRestoredEvent>
{
    public ValueTask Handle(FileRestoredEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesRestored(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: false);

        var relativePath = notification.RelativePath.ToString();
        if (state.TrackedDownloads.TryGetValue(relativePath, out var td)
            && td.Kind == DownloadKind.LargeFile
            && state.TrackedDownloads.TryRemove(relativePath, out var removed))
        {
            state.AddRestoreBytesDownloaded(removed.ChunkSize);
        }

        return ValueTask.CompletedTask;
    }
}

// ── FileSkippedHandler ────────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesSkipped"/> and <see cref="ProgressState.BytesSkipped"/> when a file is skipped.</summary>
internal sealed class FileSkippedHandler(ProgressState state) : INotificationHandler<FileSkippedEvent>
{
    public ValueTask Handle(FileSkippedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesSkipped(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: true);
        return ValueTask.CompletedTask;
    }
}

// ── RehydrationStartedHandler ─────────────────────────────────────────────────

/// <summary>Records the rehydration chunk count and total bytes when rehydration is kicked off.</summary>
internal sealed class RehydrationStartedHandler(ProgressState state) : INotificationHandler<RehydrationStartedEvent>
{
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydration(notification.ChunkCount, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}

// ── SnapshotResolvedHandler ───────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotTimestamp"/> and <see cref="ProgressState.SnapshotRootHash"/> when the snapshot is resolved.</summary>
internal sealed class SnapshotResolvedHandler(ProgressState state) : INotificationHandler<SnapshotResolvedEvent>
{
    public ValueTask Handle(SnapshotResolvedEvent notification, CancellationToken cancellationToken)
    {
        state.SnapshotTimestamp = notification.Timestamp;
        state.SnapshotRootHash  = notification.RootHash;
        return ValueTask.CompletedTask;
    }
}

// ── TreeTraversalCompleteHandler ──────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreTotalFiles"/>, <see cref="ProgressState.RestoreTotalOriginalSize"/>, and <see cref="ProgressState.TreeTraversalComplete"/>.</summary>
internal sealed class TreeTraversalCompleteHandler(ProgressState state) : INotificationHandler<TreeTraversalCompleteEvent>
{
    public ValueTask Handle(TreeTraversalCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetTreeTraversalComplete(notification.FileCount, notification.TotalOriginalSize);
        return ValueTask.CompletedTask;
    }
}

// ── TreeTraversalProgressHandler ──────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreFilesDiscovered"/> from the batched tree traversal progress event.</summary>
internal sealed class TreeTraversalProgressHandler(ProgressState state) : INotificationHandler<TreeTraversalProgressEvent>
{
    public ValueTask Handle(TreeTraversalProgressEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreFilesDiscovered(notification.FilesFound);
        return ValueTask.CompletedTask;
    }
}

// ── FileRoutedHandler ────────────────────────────────────────────────────

/// <summary>Increments the appropriate route tally based on the event's <see cref="RestoreRoute"/> value.</summary>
internal sealed class FileRoutedHandler(ProgressState state) : INotificationHandler<FileRoutedEvent>
{
    public ValueTask Handle(FileRoutedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementRoute(notification.Route);
        return ValueTask.CompletedTask;
    }
}

// ── ChunkResolutionCompleteHandler ────────────────────────────────────────────

/// <summary>Sets chunk resolution counts and byte totals from the enriched <see cref="ChunkResolutionCompleteEvent"/>.</summary>
internal sealed class ChunkResolutionCompleteHandler(ProgressState state) : INotificationHandler<ChunkResolutionCompleteEvent>
{
    public ValueTask Handle(ChunkResolutionCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetChunkResolution(notification.TotalChunks, notification.LargeCount, notification.TarCount);
        state.SetRestoreTotalChunkBytes(notification.TotalChunkBytes);
        return ValueTask.CompletedTask;
    }
}

// ── RehydrationStatusHandler ──────────────────────────────────────────────────

/// <summary>Sets chunk availability counts from the rehydration check.</summary>
internal sealed class RehydrationStatusHandler(ProgressState state) : INotificationHandler<RehydrationStatusEvent>
{
    public ValueTask Handle(RehydrationStatusEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydrationStatus(notification.Available, notification.Rehydrated, notification.NeedsRehydration, notification.Pending);
        return ValueTask.CompletedTask;
    }
}

// ── ChunkDownloadStartedHandler ───────────────────────────────────────────────

/// <summary>
/// Stores tar bundle metadata (file count + original size) in <see cref="ProgressState.TarBundleMetadata"/>
/// before the tar-bundle download progress callback is invoked, so the CLI can build display labels.
/// </summary>
internal sealed class ChunkDownloadStartedHandler(ProgressState state) : INotificationHandler<ChunkDownloadStartedEvent>
{
    public ValueTask Handle(ChunkDownloadStartedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.Type == "tar")
            state.TarBundleMetadata[notification.ChunkHash] = (notification.FileCount, notification.OriginalSize);
        return ValueTask.CompletedTask;
    }
}

// ── ChunkDownloadCompletedHandler ─────────────────────────────────────────────

/// <summary>Removes the <see cref="TrackedDownload"/> entry and increments <see cref="ProgressState.RestoreBytesDownloaded"/>.</summary>
internal sealed class ChunkDownloadCompletedHandler(ProgressState state) : INotificationHandler<ChunkDownloadCompletedEvent>
{
    public ValueTask Handle(ChunkDownloadCompletedEvent notification, CancellationToken cancellationToken)
    {
        state.TrackedDownloads.TryRemove(notification.ChunkHash.ToString(), out _);
        state.AddRestoreBytesDownloaded(notification.ChunkSize);
        return ValueTask.CompletedTask;
    }
}

// ── CleanupCompleteHandler ────────────────────────────────────────────────────

/// <summary>Reserved for future cleanup display. Currently a no-op.</summary>
internal sealed class CleanupCompleteHandler(ProgressState state) : INotificationHandler<CleanupCompleteEvent>
{
    public ValueTask Handle(CleanupCompleteEvent notification, CancellationToken cancellationToken)
    {
        _ = state; // reserved for future use
        return ValueTask.CompletedTask;
    }
}
