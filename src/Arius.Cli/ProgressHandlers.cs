using Arius.Core.Archive;
using Arius.Core.Restore;
using Mediator;

namespace Arius.Cli;

// ── 3.1 FileScannedEvent ──────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/> when file enumeration completes.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
    /// <summary>
    /// Updates shared progress state with the total number of files discovered during scanning.
    /// </summary>
    /// <param name="notification">Event containing the total number of files enumerated (`TotalFiles`).</param>
    /// <returns>A ValueTask completed once the progress state has been updated.</returns>
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
    /// Registers a tracked file for hashing using its relative path and file size.
    /// </summary>
    /// <param name="notification">The event containing the file's relative path and size to add to the progress state.</param>
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
    /// Mark a tracked file as hashed and associate its computed content hash.
    /// </summary>
    /// <param name="notification">Event containing the file's relative path and the computed content hash.</param>
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
    /// <param name="notification">Event containing the content hash of the file added to the tar.</param>
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
    /// Marks the specified content hashes as uploading in the given sealed tar bundle and records the tar identifier.
    /// </summary>
    /// <param name="notification">Event containing the content hashes included in the sealed tar and the tar's hash identifier.</param>
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
    /// Marks the file identified by the event's content hash as currently uploading in the shared progress state.
    /// </summary>
    /// <param name="notification">Event containing the content hash of the chunk whose file should be marked uploading.</param>
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
    /// Updates progress when a chunk upload completes.
    /// </summary>
    /// <param name="notification">Chunk upload event containing the content hash and compressed size.</param>
    public ValueTask Handle(ChunkUploadedEvent notification, CancellationToken cancellationToken)
    {
        if (state.ContentHashToPath.TryGetValue(notification.ContentHash, out var path))
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
    /// Finalizes a tar bundle upload by removing its tracked files and updating upload counters.
    /// </summary>
    /// <param name="notification">Event carrying the tar bundle's hash (`TarHash`) and the bundle's compressed size (`CompressedSize`).</param>
    /// <returns>The completed <see cref="ValueTask"/>.</returns>
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
    /// Marks the current snapshot as complete in the shared progress state.
    /// </summary>
    /// <returns>A completed ValueTask.</returns>
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
    /// Handles the start of a restore by recording the total number of files to restore in the shared progress state.
    /// </summary>
    /// <param name="notification">Event containing the total number of files to restore.</param>
    /// <returns>A completed ValueTask.</returns>
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
    /// Record a successfully restored file in the shared progress state.
    /// </summary>
    /// <param name="notification">The event containing the restored file's relative path and size.</param>
    /// <returns>A ValueTask that completes once the progress state has been updated.</returns>
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
    /// Record that a file was skipped during restore and update restore progress.
    /// </summary>
    /// <param name="notification">The event containing the skipped file's relative path and size.</param>
    /// <returns>A completed ValueTask.</returns>
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
    /// Records rehydration totals (chunk count and total bytes) into the shared progress state.
    /// </summary>
    /// <param name="notification">Event containing the rehydration chunk count and total byte size.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydration(notification.ChunkCount, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}
