using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Mediator;

namespace Arius.Cli.Commands.Archive;

// ── FileScannedHandler ────────────────────────────────────────────────────────

/// <summary>Increments <see cref="ProgressState.FilesScanned"/> and <see cref="ProgressState.BytesScanned"/> per file.</summary>
public sealed class FileScannedHandler(ProgressState state) : INotificationHandler<FileScannedEvent>
{
    public ValueTask Handle(FileScannedEvent notification, CancellationToken cancellationToken)
    {
        state.IncrementFilesScanned(notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── ScanCompleteHandler ───────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.TotalFiles"/>, <see cref="ProgressState.TotalBytes"/>, and <see cref="ProgressState.ScanComplete"/> when enumeration finishes.</summary>
public sealed class ScanCompleteHandler(ProgressState state) : INotificationHandler<ScanCompleteEvent>
{
    public ValueTask Handle(ScanCompleteEvent notification, CancellationToken cancellationToken)
    {
        state.SetScanComplete(notification.TotalFiles, notification.TotalBytes);
        return ValueTask.CompletedTask;
    }
}

// ── FileHashingHandler ────────────────────────────────────────────────────────

/// <summary>Adds a <see cref="TrackedFile"/> entry with State=Hashing.</summary>
public sealed class FileHashingHandler(ProgressState state) : INotificationHandler<FileHashingEvent>
{
    public ValueTask Handle(FileHashingEvent notification, CancellationToken cancellationToken)
    {
        state.AddFile(notification.RelativePath, notification.FileSize);
        return ValueTask.CompletedTask;
    }
}

// ── FileHashedHandler ─────────────────────────────────────────────────────────

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

// ── TarBundleStartedHandler ───────────────────────────────────────────────────

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

// ── TarEntryAddedHandler ──────────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedFile"/> for the small file (it moves into the TAR),
/// updates the current <see cref="TrackedTar"/> file count and accumulated bytes,
/// and increments <see cref="ProgressState.FilesUnique"/>.
/// </summary>
public sealed class TarEntryAddedHandler(ProgressState state) : INotificationHandler<TarEntryAddedEvent>
{
    public ValueTask Handle(TarEntryAddedEvent notification, CancellationToken cancellationToken)
    {
        if (state.ContentHashToPath.TryGetValue(notification.ContentHash, out var paths))
            foreach (var path in paths)
                state.RemoveFile(path);

        var tar = state.TrackedTars.Values
            .Where(t => t.State == TarState.Accumulating)
            .OrderByDescending(t => t.BundleNumber)
            .FirstOrDefault();

        if (tar != null)
        {
            var addedBytes = notification.CurrentTarSize - tar.AccumulatedBytes;
            tar.AddEntry(addedBytes > 0 ? addedBytes : 0);
        }

        state.IncrementFilesUnique();

        return ValueTask.CompletedTask;
    }
}

// ── TarBundleSealingHandler ───────────────────────────────────────────────────

/// <summary>Transitions the current <see cref="TrackedTar"/> to Sealing and sets its TarHash and TotalBytes.</summary>
public sealed class TarBundleSealingHandler(ProgressState state) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent notification, CancellationToken cancellationToken)
    {
        var tar = state.TrackedTars.Values
            .Where(t => t.State == TarState.Accumulating || t.State == TarState.Sealing)
            .OrderByDescending(t => t.BundleNumber)
            .FirstOrDefault();

        if (tar != null)
        {
            tar.TarHash    = notification.TarHash;
            tar.TotalBytes = notification.UncompressedSize;
            tar.State      = TarState.Sealing;
        }

        return ValueTask.CompletedTask;
    }
}

// ── ChunkUploadingHandler ─────────────────────────────────────────────────────

/// <summary>
/// Dual lookup: large file → transitions <see cref="TrackedFile"/> to Uploading and increments <see cref="ProgressState.FilesUnique"/>;
/// TAR bundle → transitions <see cref="TrackedTar"/> to Uploading.
/// </summary>
public sealed class ChunkUploadingHandler(ProgressState state) : INotificationHandler<ChunkUploadingEvent>
{
    public ValueTask Handle(ChunkUploadingEvent notification, CancellationToken cancellationToken)
    {
        if (state.SetFileUploading(ContentHash.Parse(notification.ChunkHash.ToString())))
        {
            state.IncrementFilesUnique();
            return ValueTask.CompletedTask;
        }

        var tar = state.TrackedTars.Values.FirstOrDefault(t => t.TarHash == notification.ChunkHash);
        if (tar != null)
            tar.State = TarState.Uploading;

        return ValueTask.CompletedTask;
    }
}

// ── ChunkUploadedHandler ──────────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedFile"/> entry (large file done)
/// and increments <see cref="ProgressState.ChunksUploaded"/> / <see cref="ProgressState.BytesUploaded"/>.
/// </summary>
public sealed class ChunkUploadedHandler(ProgressState state) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent notification, CancellationToken cancellationToken)
    {
        if (state.ContentHashToPath.TryGetValue(ContentHash.Parse(notification.ChunkHash.ToString()), out var paths))
            foreach (var path in paths)
                state.RemoveFile(path);
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── TarBundleUploadedHandler ──────────────────────────────────────────────────

/// <summary>
/// Removes the <see cref="TrackedTar"/> entry for the uploaded tar bundle
/// and increments <see cref="ProgressState.TarsUploaded"/> and <see cref="ProgressState.ChunksUploaded"/>.
/// </summary>
public sealed class TarBundleUploadedHandler(ProgressState state) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent notification, CancellationToken cancellationToken)
    {
        var entry = state.TrackedTars.FirstOrDefault(kv => kv.Value.TarHash == notification.TarHash);
        if (entry.Value != null)
            state.TrackedTars.TryRemove(entry.Key, out _);

        state.IncrementTarsUploaded();
        state.IncrementChunksUploaded(notification.CompressedSize);
        return ValueTask.CompletedTask;
    }
}

// ── SnapshotCreatedHandler ────────────────────────────────────────────────────

/// <summary>Sets <see cref="ProgressState.SnapshotComplete"/> when the snapshot is created.</summary>
public sealed class SnapshotCreatedHandler(ProgressState state) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent notification, CancellationToken cancellationToken)
    {
        state.SetSnapshotComplete();
        return ValueTask.CompletedTask;
    }
}
