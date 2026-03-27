using System.Collections.Concurrent;

namespace Arius.Cli;

// ── 2.1 FileState enum ────────────────────────────────────────────────────────

/// <summary>Lifecycle state of a file being tracked through the archive pipeline.</summary>
public enum FileState
{
    /// <summary>File is being hashed (large and small files).</summary>
    Hashing,

    /// <summary>File has been hashed and added to a tar bundle awaiting seal/upload.</summary>
    QueuedInTar,

    /// <summary>The tar containing this file is being uploaded.</summary>
    UploadingTar,

    /// <summary>Large file chunk is being uploaded directly.</summary>
    Uploading,

    /// <summary>File processing is complete (removed from display).</summary>
    Done,
}

// ── 2.2 TrackedFile class ─────────────────────────────────────────────────────

/// <summary>
/// Per-file state for a file being tracked through the archive pipeline.
/// <see cref="BytesProcessed"/> is updated via <see cref="Interlocked.Exchange(ref long, long)"/>
/// for lock-free thread safety. Other mutable fields use <see cref="Volatile"/> reads/writes.
/// </summary>
public sealed class TrackedFile
{
    public TrackedFile(string relativePath, long totalBytes)
    {
        RelativePath = relativePath;
        TotalBytes   = totalBytes;
        State        = FileState.Hashing;
    }

    /// <summary>Relative path of the file (dictionary key).</summary>
    public string RelativePath { get; }

    /// <summary>File size in bytes, set at creation.</summary>
    public long TotalBytes { get; }

    /// <summary>Content hash, set when hashing completes.</summary>
    public string? ContentHash
    {
        get => Volatile.Read(ref _contentHash);
        set => Volatile.Write(ref _contentHash, value);
    }
    private string? _contentHash;

    /// <summary>Current lifecycle state.</summary>
    public FileState State
    {
        get => (FileState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }
    private int _state;

    /// <summary>Identifier of the tar bundle this file belongs to (set at TarBundleSealingEvent).</summary>
    public string? TarId
    {
        get => Volatile.Read(ref _tarId);
        set => Volatile.Write(ref _tarId, value);
    }
    private string? _tarId;

    /// <summary>Cumulative bytes processed so far (hashing or uploading).</summary>
    public long BytesProcessed => Interlocked.Read(ref _bytesProcessed);
    private long _bytesProcessed;

    /// <summary>Updates <see cref="BytesProcessed"/> to <paramref name="value"/>.</summary>
    public void SetBytesProcessed(long value) =>
        Interlocked.Exchange(ref _bytesProcessed, value);
}

// ── 2.3-2.6 ProgressState ─────────────────────────────────────────────────────

/// <summary>
/// Shared progress state for archive and restore operations.
/// All counter updates use <see cref="Interlocked"/> operations or concurrent collections
/// for thread safety. Registered as a singleton in DI so notification handlers and the
/// display component share the same instance.
/// </summary>
public sealed class ProgressState
{
    // ── Archive: per-file state machine ──────────────────────────────────────

    /// <summary>Files currently tracked in the pipeline, keyed by relative path.</summary>
    public ConcurrentDictionary<string, TrackedFile> TrackedFiles { get; } = new();

    /// <summary>
    /// Reverse lookup: ContentHash → RelativePath.
    /// Populated when <c>FileHashedEvent</c> fires; used by downstream content-hash-keyed events.
    /// </summary>
    public ConcurrentDictionary<string, string> ContentHashToPath { get; } = new();

    /// <summary>Adds a new <see cref="TrackedFile"/> entry with State=Hashing.</summary>
    public void AddFile(string relativePath, long fileSize) =>
        TrackedFiles.TryAdd(relativePath, new TrackedFile(relativePath, fileSize));

    /// <summary>
    /// Sets <see cref="TrackedFile.ContentHash"/>, populates the reverse lookup map,
    /// and increments <see cref="FilesHashed"/>.
    /// </summary>
    public void SetFileHashed(string relativePath, string contentHash)
    {
        if (TrackedFiles.TryGetValue(relativePath, out var file))
            file.ContentHash = contentHash;
        ContentHashToPath.TryAdd(contentHash, relativePath);
        Interlocked.Increment(ref _filesHashed);
    }

    /// <summary>
    /// Transitions the file identified by <paramref name="contentHash"/> to State=QueuedInTar.
    /// Uses the <see cref="ContentHashToPath"/> reverse map to locate the entry.
    /// </summary>
    public void SetFileQueuedInTar(string contentHash)
    {
        if (ContentHashToPath.TryGetValue(contentHash, out var path) &&
            TrackedFiles.TryGetValue(path, out var file))
        {
            file.State = FileState.QueuedInTar;
        }
    }

    /// <summary>
    /// Batch-transitions all files in <paramref name="contentHashes"/> to State=UploadingTar
    /// and sets their <see cref="TrackedFile.TarId"/> to <paramref name="tarId"/>.
    /// </summary>
    public void SetFilesUploadingTar(IReadOnlyList<string> contentHashes, string tarId)
    {
        foreach (var hash in contentHashes)
        {
            if (ContentHashToPath.TryGetValue(hash, out var path) &&
                TrackedFiles.TryGetValue(path, out var file))
            {
                file.TarId = tarId;
                file.State = FileState.UploadingTar;
            }
        }
    }

    /// <summary>
    /// Transitions the file identified by <paramref name="contentHash"/> to State=Uploading.
    /// Only applies to files that are NOT on the tar path (i.e., State != QueuedInTar/UploadingTar).
    /// </summary>
    public void SetFileUploading(string contentHash)
    {
        if (ContentHashToPath.TryGetValue(contentHash, out var path) &&
            TrackedFiles.TryGetValue(path, out var file) &&
            file.State is not (FileState.QueuedInTar or FileState.UploadingTar))
        {
            file.State = FileState.Uploading;
        }
    }

    /// <summary>Removes the <see cref="TrackedFile"/> entry for <paramref name="relativePath"/>.</summary>
    public void RemoveFile(string relativePath) =>
        TrackedFiles.TryRemove(relativePath, out _);

    /// <summary>Removes all <see cref="TrackedFile"/> entries whose <see cref="TrackedFile.TarId"/> matches <paramref name="tarId"/>.</summary>
    public void RemoveFilesByTarId(string tarId)
    {
        foreach (var (path, file) in TrackedFiles)
        {
            if (file.TarId == tarId)
                TrackedFiles.TryRemove(path, out _);
        }
    }

    // ── Archive: scanning ─────────────────────────────────────────────────────

    /// <summary>Total file count; null until enumeration completes.</summary>
    public long? TotalFiles => _totalFiles < 0 ? null : _totalFiles;
    private long _totalFiles = -1;

    /// <summary>Sets the total file count once enumeration completes.</summary>
    public void SetTotalFiles(long count) => Interlocked.Exchange(ref _totalFiles, count);

    // ── Archive: hashing (aggregate counters) ────────────────────────────────

    /// <summary>Number of files for which hashing has completed.</summary>
    public long FilesHashed => Interlocked.Read(ref _filesHashed);
    private long _filesHashed;

    // ── Archive: uploading (aggregate counters) ───────────────────────────────

    /// <summary>Total chunks successfully uploaded.</summary>
    public long ChunksUploaded => Interlocked.Read(ref _chunksUploaded);
    private long _chunksUploaded;

    /// <summary>Total compressed bytes uploaded.</summary>
    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);
    private long _bytesUploaded;

    /// <summary>
    /// Total number of chunks to upload (known after dedup completes).
    /// <c>null</c> while dedup is still in progress.
    /// </summary>
    public long? TotalChunks => _totalChunks < 0 ? null : _totalChunks;
    private long _totalChunks = -1;

    public void IncrementChunksUploaded(long compressedSize)
    {
        Interlocked.Increment(ref _chunksUploaded);
        Interlocked.Add(ref _bytesUploaded, compressedSize);
    }

    public void SetTotalChunks(long count) => Interlocked.Exchange(ref _totalChunks, count);

    // ── Archive: tar bundles (aggregate counters) ─────────────────────────────

    /// <summary>Number of tar bundles successfully uploaded.</summary>
    public long TarsUploaded => Interlocked.Read(ref _tarsUploaded);
    private long _tarsUploaded;

    public void IncrementTarsUploaded() => Interlocked.Increment(ref _tarsUploaded);

    // ── Archive: snapshot ─────────────────────────────────────────────────────

    /// <summary>True once the snapshot has been created.</summary>
    public bool SnapshotComplete => Volatile.Read(ref _snapshotComplete);
    private bool _snapshotComplete;

    public void SetSnapshotComplete() => Volatile.Write(ref _snapshotComplete, true);

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>Total files to restore (set at restore start).</summary>
    public int RestoreTotalFiles => (int)Interlocked.Read(ref _restoreTotalFiles);
    private long _restoreTotalFiles;

    /// <summary>Files successfully restored to disk.</summary>
    public long FilesRestored => Interlocked.Read(ref _filesRestored);
    private long _filesRestored;

    /// <summary>Files skipped (already present with matching hash).</summary>
    public long FilesSkipped => Interlocked.Read(ref _filesSkipped);
    private long _filesSkipped;

    /// <summary>Chunks for which rehydration was kicked off.</summary>
    public int RehydrationChunkCount => (int)Interlocked.Read(ref _rehydrationChunkCount);
    private long _rehydrationChunkCount;

    public void SetRestoreTotalFiles(int count) => Interlocked.Exchange(ref _restoreTotalFiles, count);
    public void IncrementFilesRestored() => Interlocked.Increment(ref _filesRestored);
    public void IncrementFilesSkipped() => Interlocked.Increment(ref _filesSkipped);
    public void SetRehydrationChunkCount(int count) => Interlocked.Exchange(ref _rehydrationChunkCount, count);
}
