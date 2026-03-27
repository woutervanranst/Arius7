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
    /// <summary>
    /// Initializes a new TrackedFile for the specified relative path and file size and sets its initial state to Hashing.
    /// </summary>
    /// <param name="relativePath">The file's relative path used as the tracking key.</param>
    /// <param name="totalBytes">The file's total size in bytes.</param>
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

    /// <summary>
        /// Sets the stored number of bytes processed for this tracked file.
        /// </summary>
        /// <param name="value">The new processed byte count.</param>
    public void SetBytesProcessed(long value) =>
        Interlocked.Exchange(ref _bytesProcessed, value);
}

// ── RestoreFileEvent ──────────────────────────────────────────────────────────

/// <summary>
/// Represents one entry in the restore display tail — a single file that was
/// either restored or skipped during a restore operation.
/// </summary>
/// <param name="RelativePath">Forward-slash relative path of the file.</param>
/// <param name="FileSize">Uncompressed file size in bytes.</param>
/// <param name="Skipped">True if the file was skipped (already present); false if restored.</param>
internal sealed record RestoreFileEvent(string RelativePath, long FileSize, bool Skipped);

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
    /// <summary>
    /// Records a file's computed content hash and updates archive progress state.
    /// </summary>
    /// <param name="relativePath">The file's relative path within the archive.</param>
    /// <param name="contentHash">The computed content hash for the file.</param>
    /// <remarks>
    /// If the file is tracked, its ContentHash is set; a mapping from content hash to path is added and the hashed-files counter is incremented.
    /// </remarks>
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
    /// <summary>
    /// Marks the tracked file identified by the given content hash as queued in a tar bundle.
    /// </summary>
    /// <param name="contentHash">The content hash used to locate the tracked file via ContentHashToPath; if no matching tracked file is found, no change is made.</param>
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
    /// <summary>
    /// Marks the tracked files identified by the given content hashes as being uploaded in the specified tar bundle.
    /// </summary>
    /// <param name="contentHashes">Content hashes identifying files to mark as part of the tar bundle.</param>
    /// <param name="tarId">The identifier of the tar bundle assigned to those files.</param>
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
    /// <summary>
    /// Set the tracked file's state to Uploading for the file matching the given content hash, unless that file is currently in state QueuedInTar or UploadingTar. If no tracked file is found for the hash, no change is made.
    /// </summary>
    /// <param name="contentHash">Content hash used to locate the tracked file.</param>
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

    /// <summary>
    /// Record the completion of a uploaded chunk and add its compressed size to the uploaded-byte total.
    /// </summary>
    /// <param name="compressedSize">Size in bytes of the compressed chunk to add to the uploaded total.</param>
    public void IncrementChunksUploaded(long compressedSize)
    {
        Interlocked.Increment(ref _chunksUploaded);
        Interlocked.Add(ref _bytesUploaded, compressedSize);
    }

    /// <summary>
/// Updates the recorded total number of archive chunks; a negative value marks the total as unset.
/// </summary>
/// <param name="count">The total chunk count to store; use a value less than zero to indicate the total is unknown/unset.</param>
public void SetTotalChunks(long count) => Interlocked.Exchange(ref _totalChunks, count);

    // ── Archive: tar bundles (aggregate counters) ─────────────────────────────

    /// <summary>Number of tar bundles successfully uploaded.</summary>
    public long TarsUploaded => Interlocked.Read(ref _tarsUploaded);
    private long _tarsUploaded;

    /// <summary>
/// Atomically increments the recorded count of uploaded tar bundles.
/// </summary>
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

    /// <summary>Total bytes of files written to disk.</summary>
    public long BytesRestored => Interlocked.Read(ref _bytesRestored);
    private long _bytesRestored;

    /// <summary>Total bytes of files skipped.</summary>
    public long BytesSkipped => Interlocked.Read(ref _bytesSkipped);
    private long _bytesSkipped;

    /// <summary>Chunks for which rehydration was kicked off.</summary>
    public int RehydrationChunkCount => (int)Interlocked.Read(ref _rehydrationChunkCount);
    private long _rehydrationChunkCount;

    /// <summary>Total bytes covered by the rehydration request.</summary>
    public long RehydrationTotalBytes => Interlocked.Read(ref _rehydrationTotalBytes);
    private long _rehydrationTotalBytes;

    /// <summary>Rolling window of the 10 most recent restore file events, for display tail.</summary>
    internal ConcurrentQueue<RestoreFileEvent> RecentRestoreEvents { get; } = new();

    /// <summary>
/// Sets the expected total number of files to restore.
/// </summary>
/// <param name="count">The total number of files in the restore operation.</param>
public void SetRestoreTotalFiles(int count) => Interlocked.Exchange(ref _restoreTotalFiles, count);

    /// <summary>
    /// Record a restored file by incrementing the restored-files count and adding its size to the total restored bytes.
    /// </summary>
    /// <param name="fileSize">Size of the restored file in bytes (uncompressed) to add to the aggregate.</param>
    public void IncrementFilesRestored(long fileSize)
    {
        Interlocked.Increment(ref _filesRestored);
        Interlocked.Add(ref _bytesRestored, fileSize);
    }

    /// <summary>
    /// Increments the count of skipped files and adds the skipped file's size to the total skipped bytes.
    /// </summary>
    /// <param name="fileSize">Size of the skipped file in bytes.</param>
    public void IncrementFilesSkipped(long fileSize)
    {
        Interlocked.Increment(ref _filesSkipped);
        Interlocked.Add(ref _bytesSkipped, fileSize);
    }

    /// <summary>
    /// Set the expected rehydration workload by storing the number of chunks to rehydrate and the total bytes across those chunks.
    /// </summary>
    /// <param name="count">The total number of rehydration chunks.</param>
    /// <param name="bytes">The total number of bytes to be rehydrated.</param>
    public void SetRehydration(int count, long bytes)
    {
        Interlocked.Exchange(ref _rehydrationChunkCount, count);
        Interlocked.Exchange(ref _rehydrationTotalBytes, bytes);
    }

    /// <summary>
    /// Enqueues a restore file event into <see cref="RecentRestoreEvents"/>, capped at 10 entries.
    /// If the queue already has 10 entries the oldest is dequeued first.
    /// <summary>
    /// Adds a restore event to the recent restore events queue, maintaining a maximum of 10 entries.
    /// </summary>
    /// <param name="path">Relative path of the restored or skipped file.</param>
    /// <param name="size">Uncompressed size of the file in bytes.</param>
    /// <param name="skipped">`true` if the file was skipped during restore, `false` if it was restored.</param>
    public void AddRestoreEvent(string path, long size, bool skipped)
    {
        // Trim to cap before adding so we never exceed 10.
        while (RecentRestoreEvents.Count >= 10)
            RecentRestoreEvents.TryDequeue(out _);
        RecentRestoreEvents.Enqueue(new RestoreFileEvent(path, size, skipped));
    }
}
