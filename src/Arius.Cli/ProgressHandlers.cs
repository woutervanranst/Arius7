using Arius.Core.Archive;
using Arius.Core.Restore;
using Mediator;

namespace Arius.Cli;

// ── 2.1 FileScannedEvent ─────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/> when file enumeration completes.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
    public ValueTask Handle(FileScannedEvent notification, CancellationToken cancellationToken)
    {
        state.SetTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 2.2 FileHashingEvent ─────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesHashing"/> and records the current file name.</summary>
public sealed class FileHashingHandler(ProgressState state) : INotificationHandler<FileHashingEvent>
{
    public ValueTask Handle(FileHashingEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesHashing(notification.RelativePath, notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── 2.3 FileHashedEvent ──────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesHashed"/> and decrements <see cref="ProgressState.FilesHashing"/>.</summary>
public sealed class FileHashedHandler(ProgressState state) : INotificationHandler<FileHashedEvent>
{
    public ValueTask Handle(FileHashedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesHashed();
        return ValueTask.CompletedTask;
    }
}

// ── 2.4 ChunkUploadingEvent ──────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.ChunksUploading"/> and records the current upload info.</summary>
public sealed class ChunkUploadingHandler(ProgressState state) : INotificationHandler<ChunkUploadingEvent>
{
    public ValueTask Handle(ChunkUploadingEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementChunksUploading(notification.ContentHash, notification.Size);
        return ValueTask.CompletedTask;
    }
}

// ── 2.5 ChunkUploadedEvent ───────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.ChunksUploaded"/>, decrements <see cref="ProgressState.ChunksUploading"/>, and adds bytes.</summary>
public sealed class ChunkUploadedHandler(ProgressState state) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── 2.6 TarBundleSealingEvent ────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.TarsBundled"/> when a tar bundle is sealed.</summary>
public sealed class TarBundleSealingHandler(ProgressState state) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementTarsBundled();
        return ValueTask.CompletedTask;
    }
}

// ── 2.7 TarBundleUploadedEvent ───────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.TarsUploaded"/> when a tar bundle upload completes.</summary>
public sealed class TarBundleUploadedHandler(ProgressState state) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementTarsUploaded();
        return ValueTask.CompletedTask;
    }
}

// ── 2.8 SnapshotCreatedEvent ─────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotComplete"/> when the snapshot is created.</summary>
public sealed class SnapshotCreatedHandler(ProgressState state) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent notification, CancellationToken cancellationToken)
    {
        state.SetSnapshotComplete();
        return ValueTask.CompletedTask;
    }
}

// ── 3.1 RestoreStartedEvent ──────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.RestoreTotalFiles"/> when restore begins.</summary>
public sealed class RestoreStartedHandler(ProgressState state) : INotificationHandler<RestoreStartedEvent>
{
    public ValueTask Handle(RestoreStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRestoreTotalFiles(notification.TotalFiles);
        return ValueTask.CompletedTask;
    }
}

// ── 3.2 FileRestoredEvent ────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesRestored"/> when a file is written to disk.</summary>
public sealed class FileRestoredHandler(ProgressState state) : INotificationHandler<FileRestoredEvent>
{
    public ValueTask Handle(FileRestoredEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesRestored();
        return ValueTask.CompletedTask;
    }
}

// ── 3.3 FileSkippedEvent ─────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesSkipped"/> when a file is skipped.</summary>
public sealed class FileSkippedHandler(ProgressState state) : INotificationHandler<FileSkippedEvent>
{
    public ValueTask Handle(FileSkippedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesSkipped();
        return ValueTask.CompletedTask;
    }
}

// ── 3.4 RehydrationStartedEvent ──────────────────────────────────────────────

/// <summary>Records the rehydration chunk count when rehydration is kicked off.</summary>
public sealed class RehydrationStartedHandler(ProgressState state) : INotificationHandler<RehydrationStartedEvent>
{
    public ValueTask Handle(RehydrationStartedEvent notification, CancellationToken cancellationToken)
    {
        state.SetRehydrationChunkCount(notification.ChunkCount);
        return ValueTask.CompletedTask;
    }
}
