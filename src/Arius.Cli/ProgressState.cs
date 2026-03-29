using System.Collections.Concurrent;

namespace Arius.Cli;

// ── FileState enum ────────────────────────────────────────────────────────────

/// <summary>Lifecycle state of a file being tracked through the archive pipeline.</summary>
public enum FileState
{
    /// <summary>File is being hashed (large and small files). Visible in display.</summary>
    Hashing,

    /// <summary>File has been hashed; invisible in display. Kept for ContentHash lookup.</summary>
    Hashed,

    /// <summary>Large file chunk is being uploaded directly. Visible in display.</summary>
    Uploading,

    /// <summary>File processing is complete (removed from display).</summary>
    Done,
}

// ── TrackedFile class ─────────────────────────────────────────────────────────

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

    /// <summary>Cumulative bytes processed so far (hashing or uploading).</summary>
    public long BytesProcessed => Interlocked.Read(ref _bytesProcessed);
    private long _bytesProcessed;

    /// <summary>Sets the stored number of bytes processed for this tracked file.</summary>
    /// <param name="value">The new processed byte count.</param>
    public void SetBytesProcessed(long value) =>
        Interlocked.Exchange(ref _bytesProcessed, value);
}

// ── TarState enum ─────────────────────────────────────────────────────────────

/// <summary>Lifecycle state of a TAR bundle being tracked through the archive pipeline.</summary>
public enum TarState
{
    /// <summary>TAR is accepting small files (growing).</summary>
    Accumulating,

    /// <summary>TAR has been sealed; hash is being computed.</summary>
    Sealing,

    /// <summary>TAR bundle is being uploaded.</summary>
    Uploading,
}

// ── TrackedTar class ──────────────────────────────────────────────────────────

/// <summary>
/// Per-TAR state for a bundle being accumulated or uploaded.
/// Interlocked / Volatile used for thread-safe field updates.
/// </summary>
public sealed class TrackedTar
{
    /// <summary>Sequential display number (1-based), assigned by the CLI handler.</summary>
    public int BundleNumber { get; }

    /// <summary>Target uncompressed size (TarTargetSize) used for the accumulation progress bar denominator.</summary>
    public long TargetSize { get; }

    /// <summary>
    /// Initializes a new <see cref="TrackedTar"/> with the given bundle number and target size.
    /// State is set to <see cref="TarState.Accumulating"/>.
    /// </summary>
    public TrackedTar(int bundleNumber, long targetSize)
    {
        BundleNumber = bundleNumber;
        TargetSize   = targetSize;
        State        = TarState.Accumulating;
    }

    /// <summary>Current lifecycle state.</summary>
    public TarState State
    {
        get => (TarState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }
    private int _state;

    /// <summary>Number of files accumulated so far.</summary>
    public int FileCount => (int)Interlocked.Read(ref _fileCount);
    private long _fileCount;

    /// <summary>Total uncompressed bytes of files accumulated so far.</summary>
    public long AccumulatedBytes => Interlocked.Read(ref _accumulatedBytes);
    private long _accumulatedBytes;

    /// <summary>Final uncompressed size set when the TAR is sealed.</summary>
    public long TotalBytes
    {
        get => Interlocked.Read(ref _totalBytes);
        set => Interlocked.Exchange(ref _totalBytes, value);
    }
    private long _totalBytes;

    /// <summary>Content hash of the sealed TAR file (set at sealing).</summary>
    public string? TarHash
    {
        get => Volatile.Read(ref _tarHash);
        set => Volatile.Write(ref _tarHash, value);
    }
    private string? _tarHash;

    /// <summary>Cumulative bytes uploaded so far (set via ProgressStream callback).</summary>
    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);
    private long _bytesUploaded;

    /// <summary>Increments file count and accumulated bytes when a new entry is added.</summary>
    public void AddEntry(long fileSize)
    {
        Interlocked.Increment(ref _fileCount);
        Interlocked.Add(ref _accumulatedBytes, fileSize);
    }

    /// <summary>Atomically updates the bytes-uploaded counter.</summary>
    public void SetBytesUploaded(long value) =>
        Interlocked.Exchange(ref _bytesUploaded, value);
}

// ── TrackedDownload class ─────────────────────────────────────────────────

/// <summary>
/// Per-chunk state for a download being tracked through the restore pipeline.
/// Present in <see cref="ProgressState.TrackedDownloads"/> while downloading;
/// removed on completion. <see cref="BytesDownloaded"/> is updated via
/// <see cref="Interlocked.Exchange(ref long, long)"/> for lock-free thread safety.
/// </summary>
public sealed class TrackedDownload
{
    /// <summary>
    /// Initializes a new TrackedDownload for the specified chunk.
    /// </summary>
    /// <param name="key">Chunk hash used as dictionary key.</param>
    /// <param name="kind">Whether this is a large file or tar bundle download.</param>
    /// <param name="displayName">Human-readable label for display (file path or "TAR bundle (N files, X)").</param>
    /// <param name="compressedSize">Total compressed download size in bytes.</param>
    /// <param name="originalSize">Sum of original file sizes for this chunk.</param>
    public TrackedDownload(string key, Core.Restore.DownloadKind kind, string displayName, long compressedSize, long originalSize)
    {
        Key            = key;
        Kind           = kind;
        DisplayName    = displayName;
        CompressedSize = compressedSize;
        OriginalSize   = originalSize;
    }

    /// <summary>Chunk hash, used as dictionary key.</summary>
    public string Key { get; }

    /// <summary>Whether this is a large file or tar bundle download.</summary>
    public Core.Restore.DownloadKind Kind { get; }

    /// <summary>Human-readable label: file relative path for large files, "TAR bundle (N files, X)" for tar bundles.</summary>
    public string DisplayName { get; }

    /// <summary>Total compressed download size in bytes.</summary>
    public long CompressedSize { get; }

    /// <summary>Sum of original file sizes for this chunk.</summary>
    public long OriginalSize { get; }

    /// <summary>Cumulative bytes downloaded so far.</summary>
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    private long _bytesDownloaded;

    /// <summary>Atomically updates the bytes-downloaded counter.</summary>
    /// <param name="value">The new downloaded byte count.</param>
    public void SetBytesDownloaded(long value) =>
        Interlocked.Exchange(ref _bytesDownloaded, value);
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

// ── ProgressState ─────────────────────────────────────────────────────────────

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
    /// Reverse lookup: ContentHash → one or more RelativePaths.
    /// One-to-many because two files can legitimately hash to the same content in one run.
    /// Populated when <c>FileHashedEvent</c> fires; used by downstream content-hash-keyed events.
    /// </summary>
    public ConcurrentDictionary<string, ConcurrentBag<string>> ContentHashToPath { get; } = new();

    /// <summary>Adds a new <see cref="TrackedFile"/> entry with State=Hashing.</summary>
    public void AddFile(string relativePath, long fileSize) =>
        TrackedFiles.TryAdd(relativePath, new TrackedFile(relativePath, fileSize));

    /// <summary>
    /// Sets <see cref="TrackedFile.ContentHash"/>, populates the reverse lookup map,
    /// and increments <see cref="FilesHashed"/>.
    /// </summary>
    /// <param name="relativePath">The file's relative path within the archive.</param>
    /// <param name="contentHash">The computed content hash for the file.</param>
    public void SetFileHashed(string relativePath, string contentHash)
    {
        if (TrackedFiles.TryGetValue(relativePath, out var file))
        {
            file.ContentHash = contentHash;
            file.State       = FileState.Hashed;
        }
        ContentHashToPath.GetOrAdd(contentHash, _ => new ConcurrentBag<string>()).Add(relativePath);
        Interlocked.Increment(ref _filesHashed);
    }

    /// <summary>
    /// Transitions the file identified by <paramref name="contentHash"/> to State=Uploading
    /// and resets its BytesProcessed to 0. Only applies to large-file path (State == Hashed).
    /// </summary>
    /// <summary>
    /// Transitions all <see cref="TrackedFile"/> entries for <paramref name="contentHash"/> from
    /// <see cref="FileState.Hashed"/> to <see cref="FileState.Uploading"/>.
    /// </summary>
    /// <returns><c>true</c> if at least one file was transitioned; <c>false</c> if the hash is unknown (TAR path).</returns>
    public bool SetFileUploading(string contentHash)
    {
        if (!ContentHashToPath.TryGetValue(contentHash, out var paths))
            return false;

        var transitioned = false;
        foreach (var path in paths)
            if (TrackedFiles.TryGetValue(path, out var file) && file.State == FileState.Hashed)
            {
                file.SetBytesProcessed(0);
                file.State = FileState.Uploading;
                transitioned = true;
            }
        return transitioned;
    }

    /// <summary>Removes the <see cref="TrackedFile"/> entry for <paramref name="relativePath"/>.</summary>
    public void RemoveFile(string relativePath) =>
        TrackedFiles.TryRemove(relativePath, out _);

    // ── Archive: TAR bundle tracking ─────────────────────────────────────────

    /// <summary>TAR bundles currently tracked, keyed by bundle number.</summary>
    public ConcurrentDictionary<int, TrackedTar> TrackedTars { get; } = new();

    /// <summary>Monotonically increasing bundle counter; call <see cref="NextBundleNumber"/> to allocate a new ID.</summary>
    private long _bundleCounter;

    /// <summary>Allocates the next unique bundle number in a thread-safe manner.</summary>
    public int NextBundleNumber() => (int)Interlocked.Increment(ref _bundleCounter);

    /// <summary>Target uncompressed size for a single TAR bundle. Defaults to 64 MB; set once at startup.</summary>
    public long TarTargetSize { get; set; } = 64L * 1024 * 1024;

    // ── Archive: scanning ─────────────────────────────────────────────────────

    /// <summary>Number of files discovered so far (ticks up per FileScannedEvent).</summary>
    public long FilesScanned => Interlocked.Read(ref _filesScanned);
    private long _filesScanned;

    /// <summary>Total bytes of all scanned files discovered so far.</summary>
    public long BytesScanned => Interlocked.Read(ref _bytesScanned);
    private long _bytesScanned;

    /// <summary>True once ScanCompleteEvent fires (enumeration finished).</summary>
    public bool ScanComplete => Volatile.Read(ref _scanComplete);
    private bool _scanComplete;

    /// <summary>Total files count (from ScanCompleteEvent); null until enumeration completes.</summary>
    public long? TotalFiles => _totalFiles < 0 ? null : _totalFiles;
    private long _totalFiles = -1;

    /// <summary>Total bytes of all enumerated files (from ScanCompleteEvent).</summary>
    public long TotalBytes
    {
        get => Interlocked.Read(ref _totalBytes);
    }
    private long _totalBytes;

    /// <summary>Increments the live scan counter and byte total (called per FileScannedEvent).</summary>
    public void IncrementFilesScanned(long fileSize)
    {
        Interlocked.Increment(ref _filesScanned);
        Interlocked.Add(ref _bytesScanned, fileSize);
    }

    /// <summary>Sets TotalFiles + TotalBytes + ScanComplete when enumeration finishes.</summary>
    public void SetScanComplete(long totalFiles, long totalBytes)
    {
        Interlocked.Exchange(ref _totalFiles, totalFiles);
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        Volatile.Write(ref _scanComplete, true);
    }

    // ── Archive: hashing (aggregate counters) ────────────────────────────────

    /// <summary>Number of files for which hashing has completed.</summary>
    public long FilesHashed => Interlocked.Read(ref _filesHashed);
    private long _filesHashed;

    /// <summary>Number of files confirmed unique (not deduped); incremented per upload routed.</summary>
    public long FilesUnique => Interlocked.Read(ref _filesUnique);
    private long _filesUnique;

    /// <summary>Increments the unique-files counter.</summary>
    public void IncrementFilesUnique() => Interlocked.Increment(ref _filesUnique);

    // ── Archive: queue depth (set by pipeline via OnHashQueueReady/OnUploadQueueReady) ──────

    /// <summary>
    /// Getter for hash-stage queue depth; null until pipeline sets it via OnHashQueueReady callback.
    /// </summary>
    public Func<int>? HashQueueDepth
    {
        get => Volatile.Read(ref _hashQueueDepth);
        set => Volatile.Write(ref _hashQueueDepth, value);
    }
    private Func<int>? _hashQueueDepth;

    /// <summary>
    /// Getter for upload-stage queue depth; null until pipeline sets it via OnUploadQueueReady callback.
    /// </summary>
    public Func<int>? UploadQueueDepth
    {
        get => Volatile.Read(ref _uploadQueueDepth);
        set => Volatile.Write(ref _uploadQueueDepth, value);
    }
    private Func<int>? _uploadQueueDepth;

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
    /// Record the completion of an uploaded chunk and add its compressed size to the uploaded-byte total.
    /// </summary>
    /// <param name="compressedSize">Size in bytes of the compressed chunk to add to the uploaded total.</param>
    public void IncrementChunksUploaded(long compressedSize)
    {
        Interlocked.Increment(ref _chunksUploaded);
        Interlocked.Add(ref _bytesUploaded, compressedSize);
    }

    /// <summary>Updates the recorded total number of archive chunks.</summary>
    public void SetTotalChunks(long count) => Interlocked.Exchange(ref _totalChunks, count);

    // ── Archive: tar bundles (aggregate counters) ─────────────────────────────

    /// <summary>Number of tar bundles successfully uploaded.</summary>
    public long TarsUploaded => Interlocked.Read(ref _tarsUploaded);
    private long _tarsUploaded;

    /// <summary>Atomically increments the recorded count of uploaded tar bundles.</summary>
    public void IncrementTarsUploaded() => Interlocked.Increment(ref _tarsUploaded);

    // ── Archive: snapshot ─────────────────────────────────────────────────────

    /// <summary>True once the snapshot has been created.</summary>
    public bool SnapshotComplete => Volatile.Read(ref _snapshotComplete);
    private bool _snapshotComplete;

    /// <summary>Marks the snapshot as complete.</summary>
    public void SetSnapshotComplete() => Volatile.Write(ref _snapshotComplete, true);

    // ── Restore ───────────────────────────────────────────────────────────────

    // ── Restore: active downloads ────────────────────────────────────────────

    /// <summary>Active downloads tracked during restore, keyed by chunk hash.</summary>
    public ConcurrentDictionary<string, TrackedDownload> TrackedDownloads { get; } = new(StringComparer.Ordinal);

    // ── Restore: tree traversal progress ─────────────────────────────────────

    /// <summary>Number of files discovered so far during tree traversal (ticks up per <c>TreeTraversalProgressEvent</c>).</summary>
    public long RestoreFilesDiscovered => Interlocked.Read(ref _restoreFilesDiscovered);
    private long _restoreFilesDiscovered;

    /// <summary>Sets the number of files discovered during tree traversal.</summary>
    public void SetRestoreFilesDiscovered(long count) => Interlocked.Exchange(ref _restoreFilesDiscovered, count);

    // ── Restore: aggregate byte totals from chunk resolution ─────────────────

    /// <summary>Total compressed download bytes (denominator for aggregate download progress bar).</summary>
    public long RestoreTotalCompressedBytes => Interlocked.Read(ref _restoreTotalCompressedBytes);
    private long _restoreTotalCompressedBytes;

    /// <summary>Sets the total compressed download bytes from chunk resolution.</summary>
    public void SetRestoreTotalCompressedBytes(long bytes) => Interlocked.Exchange(ref _restoreTotalCompressedBytes, bytes);

    /// <summary>Cumulative compressed bytes downloaded across all chunks (numerator for aggregate download progress bar).</summary>
    public long RestoreBytesDownloaded => Interlocked.Read(ref _restoreBytesDownloaded);
    private long _restoreBytesDownloaded;

    /// <summary>Adds compressed bytes to the download counter when a chunk completes.</summary>
    public void AddRestoreBytesDownloaded(long bytes) => Interlocked.Add(ref _restoreBytesDownloaded, bytes);

    // ── Restore: snapshot and tree ───────────────────────────────────────────

    /// <summary>Snapshot timestamp resolved by the pipeline. <c>null</c> until <c>SnapshotResolvedEvent</c> fires.</summary>
    public DateTimeOffset? SnapshotTimestamp
    {
        get
        {
            var box = Volatile.Read(ref _snapshotTimestampBox);
            return box is DateTimeOffset dto ? dto : null;
        }
        set => Volatile.Write(ref _snapshotTimestampBox, value.HasValue ? (object)value.Value : null);
    }
    private object? _snapshotTimestampBox;

    /// <summary>Root hash of the resolved snapshot. <c>null</c> until <c>SnapshotResolvedEvent</c> fires.</summary>
    public string? SnapshotRootHash
    {
        get => Volatile.Read(ref _snapshotRootHash);
        set => Volatile.Write(ref _snapshotRootHash, value);
    }
    private string? _snapshotRootHash;

    /// <summary>True once tree traversal completes and all file entries are known.</summary>
    public bool TreeTraversalComplete => Volatile.Read(ref _treeTraversalComplete);
    private bool _treeTraversalComplete;

    /// <summary>Total uncompressed size of all files in the tree (set by <c>TreeTraversalCompleteEvent</c>).</summary>
    public long RestoreTotalOriginalSize => Interlocked.Read(ref _restoreTotalOriginalSize);
    private long _restoreTotalOriginalSize;

    /// <summary>Marks tree traversal as complete and sets the total original size.</summary>
    public void SetTreeTraversalComplete(int fileCount, long totalOriginalSize)
    {
        Interlocked.Exchange(ref _restoreTotalFiles, fileCount);
        Interlocked.Exchange(ref _restoreTotalOriginalSize, totalOriginalSize);
        Volatile.Write(ref _treeTraversalComplete, true);
    }

    // ── Restore: disposition tallies ─────────────────────────────────────────

    /// <summary>Count of files with disposition New (not yet on disk).</summary>
    public int DispositionNew => (int)Interlocked.Read(ref _dispositionNew);
    private long _dispositionNew;

    /// <summary>Count of files skipped because local copy is identical.</summary>
    public int DispositionSkipIdentical => (int)Interlocked.Read(ref _dispositionSkipIdentical);
    private long _dispositionSkipIdentical;

    /// <summary>Count of files overwritten (--overwrite flag set).</summary>
    public int DispositionOverwrite => (int)Interlocked.Read(ref _dispositionOverwrite);
    private long _dispositionOverwrite;

    /// <summary>Count of files kept because local differs and --overwrite not set.</summary>
    public int DispositionKeepLocalDiffers => (int)Interlocked.Read(ref _dispositionKeepLocalDiffers);
    private long _dispositionKeepLocalDiffers;

    /// <summary>Increments the disposition tally for the specified disposition.</summary>
    public void IncrementDisposition(Core.Restore.RestoreDisposition disposition)
    {
        switch (disposition)
        {
            case Core.Restore.RestoreDisposition.New:
                Interlocked.Increment(ref _dispositionNew);
                break;
            case Core.Restore.RestoreDisposition.SkipIdentical:
                Interlocked.Increment(ref _dispositionSkipIdentical);
                break;
            case Core.Restore.RestoreDisposition.Overwrite:
                Interlocked.Increment(ref _dispositionOverwrite);
                break;
            case Core.Restore.RestoreDisposition.KeepLocalDiffers:
                Interlocked.Increment(ref _dispositionKeepLocalDiffers);
                break;
        }
    }

    // ── Restore: file counts ─────────────────────────────────────────────────

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

    /// <summary>Sets the expected total number of files to restore.</summary>
    public void SetRestoreTotalFiles(int count) => Interlocked.Exchange(ref _restoreTotalFiles, count);

    /// <summary>
    /// Record a restored file by incrementing the restored-files count and adding its size to the total restored bytes.
    /// </summary>
    public void IncrementFilesRestored(long fileSize)
    {
        Interlocked.Increment(ref _filesRestored);
        Interlocked.Add(ref _bytesRestored, fileSize);
    }

    /// <summary>
    /// Increments the count of skipped files and adds the skipped file's size to the total skipped bytes.
    /// </summary>
    public void IncrementFilesSkipped(long fileSize)
    {
        Interlocked.Increment(ref _filesSkipped);
        Interlocked.Add(ref _bytesSkipped, fileSize);
    }

    /// <summary>
    /// Set the expected rehydration workload by storing the number of chunks to rehydrate and the total bytes across those chunks.
    /// </summary>
    public void SetRehydration(int count, long bytes)
    {
        Interlocked.Exchange(ref _rehydrationChunkCount, count);
        Interlocked.Exchange(ref _rehydrationTotalBytes, bytes);
    }

    // ── Restore: chunk resolution and rehydration status ─────────────────────

    /// <summary>Number of distinct chunk groups (unique content hashes) to download.</summary>
    public int ChunkGroups => (int)Interlocked.Read(ref _chunkGroups);
    private long _chunkGroups;

    /// <summary>Number of large-file chunks.</summary>
    public int LargeChunkCount => (int)Interlocked.Read(ref _largeChunkCount);
    private long _largeChunkCount;

    /// <summary>Number of tar-bundle chunks.</summary>
    public int TarChunkCount => (int)Interlocked.Read(ref _tarChunkCount);
    private long _tarChunkCount;

    /// <summary>Sets chunk resolution counts.</summary>
    public void SetChunkResolution(int chunkGroups, int largeCount, int tarCount)
    {
        Interlocked.Exchange(ref _chunkGroups, chunkGroups);
        Interlocked.Exchange(ref _largeChunkCount, largeCount);
        Interlocked.Exchange(ref _tarChunkCount, tarCount);
    }

    /// <summary>Chunks available for immediate download (Hot/Cool tier).</summary>
    public int ChunksAvailable => (int)Interlocked.Read(ref _chunksAvailable);
    private long _chunksAvailable;

    /// <summary>Chunks already rehydrated (ready to download).</summary>
    public int ChunksRehydrated => (int)Interlocked.Read(ref _chunksRehydrated);
    private long _chunksRehydrated;

    /// <summary>Chunks needing rehydration from Archive tier.</summary>
    public int ChunksNeedingRehydration => (int)Interlocked.Read(ref _chunksNeedingRehydration);
    private long _chunksNeedingRehydration;

    /// <summary>Chunks currently being rehydrated (pending from a previous run).</summary>
    public int ChunksPending => (int)Interlocked.Read(ref _chunksPending);
    private long _chunksPending;

    /// <summary>Sets rehydration availability status counts.</summary>
    public void SetRehydrationStatus(int available, int rehydrated, int needsRehydration, int pending)
    {
        Interlocked.Exchange(ref _chunksAvailable, available);
        Interlocked.Exchange(ref _chunksRehydrated, rehydrated);
        Interlocked.Exchange(ref _chunksNeedingRehydration, needsRehydration);
        Interlocked.Exchange(ref _chunksPending, pending);
    }

    // ── Restore: recent events ───────────────────────────────────────────────

    /// <summary>Rolling window of the 10 most recent restore file events, for display tail.</summary>
    internal ConcurrentQueue<RestoreFileEvent> RecentRestoreEvents { get; } = new();

    /// <summary>
    /// Enqueues a restore file event into <see cref="RecentRestoreEvents"/>, capped at 10 entries.
    /// If the queue already has 10 entries the oldest is dequeued first.
    /// </summary>
    public void AddRestoreEvent(string path, long size, bool skipped)
    {
        // Trim to cap before adding so we never exceed 10.
        while (RecentRestoreEvents.Count >= 10)
            RecentRestoreEvents.TryDequeue(out _);
        RecentRestoreEvents.Enqueue(new RestoreFileEvent(path, size, skipped));
    }
}
