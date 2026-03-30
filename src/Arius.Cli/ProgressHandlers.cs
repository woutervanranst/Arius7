using Arius.Core.Archive;
using Arius.Core.Restore;
using Mediator;

namespace Arius.Cli;

// ── 4.1 FileScannedHandler ────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesScanned"/> and <see cref="ProgressState.BytesScanned"/> per file.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
    public ValueTask Handle(FileScannedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesScanned(notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.2 ScanCompleteHandler ───────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/>, <see cref="ProgressState.TotalBytes"/>, and <see cref="ProgressState.ScanComplete"/> when enumeration finishes.</summary>
public sealed class ScanCompleteHandler(ProgressState state) : INotificationHandler<ScanCompleteEvent>
{
    public ValueTask Handle(ScanCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetScanComplete(notification.TotalFiles, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}

// ── 4.3 FileHashingHandler ────────────────────────────────────────────────────

/// <summary>Adds a <see cref="TrackedFile"/> entry with State=Hashing.</summary>
public sealed class FileHashingHandler(ProgressState state) : INotificationHandler<FileHashingEvent>
{
    public ValueTask Handle(FileHashingEvent notification, CancellationToken cancellationToken)
    {
        state.AddFile(notification.RelativePath, notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.4 FileHashedHandler ─────────────────────────────────────────────────────

/// <summary>
/// Sets <see cref="TrackedFile.ContentHash"/>, transitions to <see cref="FileState.Hashed"/>,
/// populates the reverse map, and increments <see cref="ProgressState.FilesHashed"/>.
/// </summary>
public sealed class FileHashedHandler(ProgressState state) : INotificationHandler<FileHashedEvent>
{
    public ValueTask Handle(FileHashedEvent notification, CancellationToken cancellationToken)
    {
        state.SetFileHashed(notification.RelativePath, notification.ContentHash);
        return ValueTask.CompletedTask;
    }
}

// ── 4.5 TarBundleStartedHandler ──────────────────────────────────────────────

/// <summary>Creates a new <see cref="TrackedTar"/> with the next bundle number and State=Accumulating.</summary>
public sealed class TarBundleStartedHandler(ProgressState state) : INotificationHandler<TarBundleStartedEvent>
{
    public ValueTask Handle(TarBundleStartedEvent notification, CancellationToken cancellationToken)
    {
        var bundleNumber = state.NextBundleNumber();
        var tar = new TrackedTar(bundleNumber, state.TarTargetSize);
        state.TrackedTars.TryAdd(bundleNumber, tar);
        return ValueTask.CompletedTask;
    }
}

// ── 4.6 TarEntryAddedHandler ──────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedFile"/> for the small file (it moves into the TAR),
/// updates the current <see cref="TrackedTar"/> file count and accumulated bytes,
/// and increments <see cref="ProgressState.FilesUnique"/>.
/// </summary>
public sealed class TarEntryAddedHandler(ProgressState state) : INotificationHandler<TarEntryAddedEvent>
{
    public ValueTask Handle(TarEntryAddedEvent notification, CancellationToken cancellationToken)
    {
        // Remove the small file from TrackedFiles (it's now represented by the TAR)
        if (state.ContentHashToPath.TryGetValue(notification.ContentHash, out var paths))
            foreach (var path in paths)
                state.RemoveFile(path);

        // Update the current tar (last in the dictionary by bundle number)
        if (state.TrackedTars.Count > 0)
        {
            var maxKey = state.TrackedTars.Keys.Max();
            if (state.TrackedTars.TryGetValue(maxKey, out var tar))
            {
                // Derive file size from the current tar size delta
                var addedBytes = notification.CurrentTarSize - (tar.AccumulatedBytes);
                tar.AddEntry(addedBytes > 0 ? addedBytes : 0);
            }
        }

        // Small file passed dedup and was routed to TAR → it's unique
        state.IncrementFilesUnique();

        return ValueTask.CompletedTask;
    }
}

// ── 4.7 TarBundleSealingHandler ───────────────────────────────────────────────

/// <summary>
/// Transitions the current <see cref="TrackedTar"/> to Sealing and sets its TarHash and TotalBytes.
/// </summary>
public sealed class TarBundleSealingHandler(ProgressState state) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent notification, CancellationToken cancellationToken)
    {
        // Find the tar by matching the TarHash (which may have just been assigned)
        // or fall back to the highest-numbered tar in Accumulating/Sealing state
        TrackedTar? tar = state.TrackedTars.Values
            .Where(t => t.State == TarState.Accumulating || t.State == TarState.Sealing)
            .OrderByDescending(t => t.BundleNumber)
            .FirstOrDefault();

        if (tar != null)
        {
            tar.TarHash   = notification.TarHash;
            tar.TotalBytes = notification.UncompressedSize;
            tar.State     = TarState.Sealing;
        }

        return ValueTask.CompletedTask;
    }
}

// ── 4.8 ChunkUploadingHandler ─────────────────────────────────────────────────

/// <summary>
/// Dual lookup: large file → transitions <see cref="TrackedFile"/> to Uploading and increments <see cref="ProgressState.FilesUnique"/>;
/// TAR bundle → transitions <see cref="TrackedTar"/> to Uploading.
/// </summary>
public sealed class ChunkUploadingHandler(ProgressState state) : INotificationHandler<ChunkUploadingEvent>
{
    public ValueTask Handle(ChunkUploadingEvent notification, CancellationToken cancellationToken)
    {
        // Try large file first; SetFileUploading returns true if it transitioned any TrackedFiles.
        if (state.SetFileUploading(notification.ContentHash))
        {
            state.IncrementFilesUnique();
            return ValueTask.CompletedTask;
        }

        // Try TAR bundle
        var tar = state.TrackedTars.Values.FirstOrDefault(t => t.TarHash == notification.ContentHash);
        if (tar != null)
            tar.State = TarState.Uploading;

        return ValueTask.CompletedTask;
    }
}

// ── 4.9 ChunkUploadedHandler ──────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedFile"/> entry (large file done)
/// and increments <see cref="ProgressState.ChunksUploaded"/> / <see cref="ProgressState.BytesUploaded"/>.
/// </summary>
public sealed class ChunkUploadedHandler(ProgressState state) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent notification, CancellationToken cancellationToken)
    {
        if (state.ContentHashToPath.TryGetValue(notification.ContentHash, out var paths))
            foreach (var path in paths)
                state.RemoveFile(path);
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.10 TarBundleUploadedHandler ────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedTar"/> entry for the uploaded tar bundle
/// and increments <see cref="ProgressState.TarsUploaded"/> and <see cref="ProgressState.ChunksUploaded"/>.
/// </summary>
public sealed class TarBundleUploadedHandler(ProgressState state) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent notification, CancellationToken cancellationToken)
    {
        // Remove the TrackedTar by its hash
        var entry = state.TrackedTars.FirstOrDefault(kv => kv.Value.TarHash == notification.TarHash);
        if (entry.Value != null)
            state.TrackedTars.TryRemove(entry.Key, out _);

        state.IncrementTarsUploaded();
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.11 SnapshotCreatedHandler ───────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotComplete"/> when the snapshot is created.</summary>
public sealed class SnapshotCreatedHandler(ProgressState state) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent notification, CancellationToken cancellationToken)
    {
        state.SetSnapshotComplete();
        return ValueTask.CompletedTask;
    }
}

// ── 4.12 RestoreStartedHandler ────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreTotalFiles"/> when restore begins.</summary>
public sealed class RestoreStartedHandler(ProgressState state) : INotificationHandler<RestoreStartedEvent>
{
    public ValueTask Handle(RestoreStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 4.13 FileRestoredHandler ──────────────────────────────────────────────────

/// <summary>
/// Increments <see cref="ProgressState.FilesRestored"/> and <see cref="ProgressState.BytesRestored"/> when a file is written to disk.
/// For large-file downloads, also removes the corresponding <see cref="TrackedDownload"/> and adds its
/// <see cref="TrackedDownload.CompressedSize"/> to <see cref="ProgressState.RestoreBytesDownloaded"/>.
/// </summary>
public sealed class FileRestoredHandler(ProgressState state) : INotificationHandler<FileRestoredEvent>
{
    public ValueTask Handle(FileRestoredEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesRestored(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: false);

        // Remove TrackedDownload for large files (keyed by RelativePath — the identifier
        // passed from RestorePipelineHandler). Tar bundles are removed by ChunkDownloadCompletedHandler.
        if (state.TrackedDownloads.TryGetValue(notification.RelativePath, out var td)
            && td.Kind == Core.Restore.DownloadKind.LargeFile
            && state.TrackedDownloads.TryRemove(notification.RelativePath, out var removed))
        {
            state.AddRestoreBytesDownloaded(removed.CompressedSize);
        }

        return ValueTask.CompletedTask;
    }
}

// ── 4.14 FileSkippedHandler ───────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesSkipped"/> and <see cref="ProgressState.BytesSkipped"/> when a file is skipped.</summary>
public sealed class FileSkippedHandler(ProgressState state) : INotificationHandler<FileSkippedEvent>
{
    public ValueTask Handle(FileSkippedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesSkipped(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: true);
        return ValueTask.CompletedTask;
    }
}

// ── 4.15 RehydrationStartedHandler ───────────────────────────────────────────

/// <summary>Records the rehydration chunk count and total bytes when rehydration is kicked off.</summary>
public sealed class RehydrationStartedHandler(ProgressState state) : INotificationHandler<RehydrationStartedEvent>
{
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydration(notification.ChunkCount, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}

// ── 4.16 SnapshotResolvedHandler ──────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotTimestamp"/> and <see cref="ProgressState.SnapshotRootHash"/> when the snapshot is resolved.</summary>
public sealed class SnapshotResolvedHandler(ProgressState state) : INotificationHandler<SnapshotResolvedEvent>
{
    public ValueTask Handle(SnapshotResolvedEvent notification, CancellationToken cancellationToken)
    {
        state.SnapshotTimestamp = notification.Timestamp;
        state.SnapshotRootHash = notification.RootHash;
        return ValueTask.CompletedTask;
    }
}

// ── 4.17 TreeTraversalCompleteHandler ─────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreTotalFiles"/>, <see cref="ProgressState.RestoreTotalOriginalSize"/>, and <see cref="ProgressState.TreeTraversalComplete"/>.</summary>
public sealed class TreeTraversalCompleteHandler(ProgressState state) : INotificationHandler<TreeTraversalCompleteEvent>
{
    public ValueTask Handle(TreeTraversalCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetTreeTraversalComplete(notification.FileCount, notification.TotalOriginalSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.18 FileDispositionHandler ───────────────────────────────────────────────

/// <summary>Increments the appropriate disposition tally based on the event's <see cref="RestoreDisposition"/> value.</summary>
public sealed class FileDispositionHandler(ProgressState state) : INotificationHandler<FileDispositionEvent>
{
    public ValueTask Handle(FileDispositionEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementDisposition(notification.Disposition);
        return ValueTask.CompletedTask;
    }
}

// ── 4.19 ChunkResolutionCompleteHandler ───────────────────────────────────────

/// <summary>Sets chunk resolution counts and byte totals from the enriched <see cref="ChunkResolutionCompleteEvent"/>.</summary>
public sealed class ChunkResolutionCompleteHandler(ProgressState state) : INotificationHandler<ChunkResolutionCompleteEvent>
{
    public ValueTask Handle(ChunkResolutionCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetChunkResolution(notification.ChunkGroups, notification.LargeCount, notification.TarCount);
        state.SetTreeTraversalComplete(state.RestoreTotalFiles, notification.TotalOriginalBytes);
        state.SetRestoreTotalCompressedBytes(notification.TotalCompressedBytes);
        return ValueTask.CompletedTask;
    }
}

// ── 4.20 RehydrationStatusHandler ─────────────────────────────────────────────

/// <summary>Sets chunk availability counts from the rehydration check.</summary>
public sealed class RehydrationStatusHandler(ProgressState state) : INotificationHandler<RehydrationStatusEvent>
{
    public ValueTask Handle(RehydrationStatusEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydrationStatus(notification.Available, notification.Rehydrated, notification.NeedsRehydration, notification.Pending);
        return ValueTask.CompletedTask;
    }
}

// ── 4.21 ChunkDownloadStartedHandler ──────────────────────────────────────────

/// <summary>
/// Stores tar bundle metadata (file count + original size) in <see cref="ProgressState.TarBundleMetadata"/>
/// before the <c>CreateDownloadProgress</c> callback is invoked, so the CLI can build display labels.
/// </summary>
public sealed class ChunkDownloadStartedHandler(ProgressState state) : INotificationHandler<ChunkDownloadStartedEvent>
{
    public ValueTask Handle(ChunkDownloadStartedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.Type == "tar")
            state.TarBundleMetadata[notification.ChunkHash] = (notification.FileCount, notification.OriginalSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.23 TreeTraversalProgressHandler ─────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreFilesDiscovered"/> from the batched tree traversal progress event.</summary>
public sealed class TreeTraversalProgressHandler(ProgressState state) : INotificationHandler<TreeTraversalProgressEvent>
{
    public ValueTask Handle(TreeTraversalProgressEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreFilesDiscovered(notification.FilesFound);
        return ValueTask.CompletedTask;
    }
}

// ── 4.24 ChunkDownloadCompletedHandler ────────────────────────────────────────

/// <summary>Removes the <see cref="TrackedDownload"/> entry and increments <see cref="ProgressState.RestoreBytesDownloaded"/>.</summary>
public sealed class ChunkDownloadCompletedHandler(ProgressState state) : INotificationHandler<ChunkDownloadCompletedEvent>
{
    public ValueTask Handle(ChunkDownloadCompletedEvent notification, CancellationToken cancellationToken)
    {
        state.TrackedDownloads.TryRemove(notification.ChunkHash, out _);
        state.AddRestoreBytesDownloaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 4.22 CleanupCompleteHandler ───────────────────────────────────────────────

/// <summary>Reserved for future cleanup display. Currently a no-op.</summary>
public sealed class CleanupCompleteHandler(ProgressState state) : INotificationHandler<CleanupCompleteEvent>
{
    public ValueTask Handle(CleanupCompleteEvent notification, CancellationToken cancellationToken)
    {
        _ = state; // reserved for future use
        return ValueTask.CompletedTask;
    }
}
