using Arius.Core.Archive;
using Arius.Core.Restore;
using Mediator;

namespace Arius.Cli;

// ── 3.1 FileScannedEvent ──────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/> when file enumeration completes.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
    /// <summary>
    /// Record the total number of files discovered during enumeration.
    /// </summary>
    /// <param name="notification">Event containing the total file count produced by the file enumeration.</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public ValueTask Handle(FileScannedEvent notification, CancellationToken cancellationToken)
    {
        state.SetTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 3.2 FileHashingEvent ──────────────────────────────────────────────────────

/// <summary>Adds a <see cref="TrackedFile"/> entry with State=Hashing.</summary>
public sealed class FileHashingHandler(ProgressState state) : INotificationHandler<FileHashingEvent>
{
    /// <summary>
    /// Registers a file that has begun hashing with the progress state using its relative path and size.
    /// </summary>
    /// <param name="notification">The event containing the file's relative path and size.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(FileHashingEvent notification, CancellationToken cancellationToken)
    {
        state.AddFile(notification.RelativePath, notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── 3.3 FileHashedEvent ───────────────────────────────────────────────────────

/// <summary>
/// Sets <see cref="TrackedFile.ContentHash"/>, populates the reverse map,
/// and increments <see cref="ProgressState.FilesHashed"/>.
/// </summary>
public sealed class FileHashedHandler(ProgressState state) : INotificationHandler<FileHashedEvent>
{
    /// <summary>
    /// Records that the specified file has been hashed in the progress state using its relative path and content hash.
    /// </summary>
    /// <param name="notification">Event containing the file's RelativePath and computed ContentHash.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(FileHashedEvent notification, CancellationToken cancellationToken)
    {
        state.SetFileHashed(notification.RelativePath, notification.ContentHash);
        return ValueTask.CompletedTask;
    }
}

// ── 3.4 TarEntryAddedEvent ────────────────────────────────────────────────────

/// <summary>Transitions the file's state to <see cref="FileState.QueuedInTar"/>.</summary>
public sealed class TarEntryAddedHandler(ProgressState state) : INotificationHandler<TarEntryAddedEvent>
{
    /// <summary>
    /// Mark the file identified by the event's content hash as queued for inclusion in a tar bundle.
    /// </summary>
    /// <param name="notification">Event containing the content hash of the file to mark as queued in the tar.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(TarEntryAddedEvent notification, CancellationToken cancellationToken)
    {
        state.SetFileQueuedInTar(notification.ContentHash);
        return ValueTask.CompletedTask;
    }
}

// ── 3.5 TarBundleSealingEvent ─────────────────────────────────────────────────

/// <summary>
/// Batch-transitions all files in the sealed tar to <see cref="FileState.UploadingTar"/>
/// and sets their <see cref="TrackedFile.TarId"/> to the tar's content hash.
/// </summary>
public sealed class TarBundleSealingHandler(ProgressState state) : INotificationHandler<TarBundleSealingEvent>
{
    /// <summary>
    /// Marks the given content hashes as members of the specified tar bundle and transitions them to the uploading-tar state in the progress tracker.
    /// </summary>
    /// <param name="notification">Event containing the content hashes of files included in the tar and the tar bundle's content hash.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(TarBundleSealingEvent notification, CancellationToken cancellationToken)
    {
        state.SetFilesUploadingTar(notification.ContentHashes, notification.TarHash);
        return ValueTask.CompletedTask;
    }
}

// ── 3.6 ChunkUploadingEvent ───────────────────────────────────────────────────

/// <summary>Transitions the file to <see cref="FileState.Uploading"/> (large file path only).</summary>
public sealed class ChunkUploadingHandler(ProgressState state) : INotificationHandler<ChunkUploadingEvent>
{
    /// <summary>
    /// Marks the file identified by the event's content hash as currently uploading.
    /// </summary>
    /// <param name="notification">Event carrying the content hash of the file whose chunk is starting upload.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(ChunkUploadingEvent notification, CancellationToken cancellationToken)
    {
        state.SetFileUploading(notification.ContentHash);
        return ValueTask.CompletedTask;
    }
}

// ── 3.7 ChunkUploadedEvent ────────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedFile"/> entry (large file done),
/// and increments <see cref="ProgressState.ChunksUploaded"/> / <see cref="ProgressState.BytesUploaded"/>.
/// </summary>
public sealed class ChunkUploadedHandler(ProgressState state) : INotificationHandler<ChunkUploadedEvent>
{
    /// <summary>
    /// Updates progress state for a completed chunk upload.
    /// </summary>
    /// <param name="notification">Event containing the chunk's content hash and compressed size.</param>
    /// <param name="cancellationToken">Cancellation token (not observed).</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(ChunkUploadedEvent notification, CancellationToken cancellationToken)
    {
        if (state.ContentHashToPath.TryGetValue(notification.ContentHash, out var paths))
            foreach (var path in paths)
                state.RemoveFile(path);
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 3.8 TarBundleUploadedEvent ────────────────────────────────────────────────

/// <summary>
/// Removes all <see cref="TrackedFile"/> entries for the uploaded tar bundle
/// and increments <see cref="ProgressState.TarsUploaded"/> and <see cref="ProgressState.ChunksUploaded"/>.
/// </summary>
public sealed class TarBundleUploadedHandler(ProgressState state) : INotificationHandler<TarBundleUploadedEvent>
{
    /// <summary>
    /// Handle a completed tar bundle upload by removing its member files from tracking and updating tar and chunk upload counters.
    /// </summary>
    /// <param name="notification">Event containing the tar bundle identifier (`TarHash`) and the bundle's `CompressedSize`.</param>
    /// <returns>A ValueTask that completes after the progress state has been updated.</returns>
    public ValueTask Handle(TarBundleUploadedEvent notification, CancellationToken cancellationToken)
    {
        state.RemoveFilesByTarId(notification.TarHash);
        state.IncrementTarsUploaded();
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 3.9 SnapshotCreatedEvent ──────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotComplete"/> when the snapshot is created.</summary>
public sealed class SnapshotCreatedHandler(ProgressState state) : INotificationHandler<SnapshotCreatedEvent>
{
    /// <summary>
    /// Marks the snapshot operation as complete in the progress tracking state.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public ValueTask Handle(SnapshotCreatedEvent notification, CancellationToken cancellationToken)
    {
        state.SetSnapshotComplete();
        return ValueTask.CompletedTask;
    }
}

// ── 4.1 RestoreStartedEvent ───────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreTotalFiles"/> when restore begins.</summary>
public sealed class RestoreStartedHandler(ProgressState state) : INotificationHandler<RestoreStartedEvent>
{
    /// <summary>
    /// Records the total number of files expected for the restore operation.
    /// </summary>
    /// <param name="notification">The restore-started event carrying the total file count.</param>
    public ValueTask Handle(RestoreStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 4.2 FileRestoredEvent ─────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesRestored"/> and <see cref="ProgressState.BytesRestored"/> when a file is written to disk.</summary>
public sealed class FileRestoredHandler(ProgressState state) : INotificationHandler<FileRestoredEvent>
{
    /// <summary>
    /// Record a restored file in the progress state and add a restore event with its size and path.
    /// </summary>
    /// <param name="notification">Event containing the restored file's RelativePath and FileSize.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(FileRestoredEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesRestored(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: false);
        return ValueTask.CompletedTask;
    }
}

// ── 4.3 FileSkippedEvent ──────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesSkipped"/> and <see cref="ProgressState.BytesSkipped"/> when a file is skipped.</summary>
public sealed class FileSkippedHandler(ProgressState state) : INotificationHandler<FileSkippedEvent>
{
    /// <summary>
    /// Records a skipped restore file in the progress state so restore metrics include its size and an entry marking it as skipped.
    /// </summary>
    /// <param name="notification">The skipped-file event containing the file's relative path and size.</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public ValueTask Handle(FileSkippedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesSkipped(notification.FileSize);
        state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: true);
        return ValueTask.CompletedTask;
    }
}

// ── 4.4 RehydrationStartedEvent ───────────────────────────────────────────────

/// <summary>Records the rehydration chunk count and total bytes when rehydration is kicked off.</summary>
public sealed class RehydrationStartedHandler(ProgressState state) : INotificationHandler<RehydrationStartedEvent>
{
    /// <summary>
    /// Records the rehydration operation's chunk count and total byte size in the progress state.
    /// </summary>
    /// <param name="notification">Event containing the rehydration chunk count and the total number of bytes to process.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydration(notification.ChunkCount, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}
