using Arius.Core.Archive;
using Arius.Core.Restore;
using Mediator;

namespace Arius.Cli;

// ── 3.1 FileScannedEvent ──────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/> when file enumeration completes.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
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
    public ValueTask Handle(RestoreStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 4.2 FileRestoredEvent ─────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesRestored"/> when a file is written to disk.</summary>
public sealed class FileRestoredHandler(ProgressState state) : INotificationHandler<FileRestoredEvent>
{
    public ValueTask Handle(FileRestoredEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesRestored();
        return ValueTask.CompletedTask;
    }
}

// ── 4.3 FileSkippedEvent ──────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesSkipped"/> when a file is skipped.</summary>
public sealed class FileSkippedHandler(ProgressState state) : INotificationHandler<FileSkippedEvent>
{
    public ValueTask Handle(FileSkippedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesSkipped();
        return ValueTask.CompletedTask;
    }
}

// ── 4.4 RehydrationStartedEvent ───────────────────────────────────────────────

/// <summary>Records the rehydration chunk count when rehydration is kicked off.</summary>
public sealed class RehydrationStartedHandler(ProgressState state) : INotificationHandler<RehydrationStartedEvent>
{
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydrationChunkCount(notification.ChunkCount);
        return ValueTask.CompletedTask;
    }
}
