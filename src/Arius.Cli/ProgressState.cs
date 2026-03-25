namespace Arius.Cli;

/// <summary>
/// Shared progress state for archive and restore operations.
/// All counter updates use <see cref="Interlocked"/> operations for thread safety.
/// Registered as a singleton in DI so notification handlers and the display component
/// share the same instance.
/// </summary>
public sealed class ProgressState
{
    // ── Archive: scanning ─────────────────────────────────────────────────────

    /// <summary>Total file count; null until enumeration completes.</summary>
    public long? TotalFiles => _totalFiles < 0 ? null : _totalFiles;
    private long _totalFiles = -1;

    /// <summary>Sets the total file count once enumeration completes.</summary>
    public void SetTotalFiles(long count) => Interlocked.Exchange(ref _totalFiles, count);

    // ── Archive: hashing ──────────────────────────────────────────────────────

    /// <summary>Number of files currently being hashed (in-flight).</summary>
    public int FilesHashing => (int)Interlocked.Read(ref _filesHashing);
    private long _filesHashing;

    /// <summary>Number of files for which hashing has completed.</summary>
    public long FilesHashed => Interlocked.Read(ref _filesHashed);
    private long _filesHashed;

    /// <summary>Name of the file currently being hashed (last set wins under concurrency).</summary>
    public volatile string? CurrentHashFile;

    /// <summary>File size of <see cref="CurrentHashFile"/> in bytes (for progress denominator).</summary>
    public long CurrentHashFileSize => Interlocked.Read(ref _currentHashFileSize);
    private long _currentHashFileSize;

    /// <summary>Bytes read so far for <see cref="CurrentHashFile"/>.</summary>
    public long CurrentHashBytesRead => Interlocked.Read(ref _currentHashBytesRead);
    private long _currentHashBytesRead;

    public void IncrementFilesHashing(string relativePath, long fileSize)
    {
        Interlocked.Increment(ref _filesHashing);
        CurrentHashFile = relativePath;
        Interlocked.Exchange(ref _currentHashFileSize, fileSize);
        Interlocked.Exchange(ref _currentHashBytesRead, 0);
    }

    public void IncrementFilesHashed()
    {
        Interlocked.Increment(ref _filesHashed);
        Interlocked.Decrement(ref _filesHashing);
    }

    public void SetHashProgress(long bytesRead) =>
        Interlocked.Exchange(ref _currentHashBytesRead, bytesRead);

    // ── Archive: uploading ────────────────────────────────────────────────────

    /// <summary>Number of chunks currently being uploaded (in-flight).</summary>
    public int ChunksUploading => (int)Interlocked.Read(ref _chunksUploading);
    private long _chunksUploading;

    /// <summary>Total chunks successfully uploaded.</summary>
    public long ChunksUploaded => Interlocked.Read(ref _chunksUploaded);
    private long _chunksUploaded;

    /// <summary>Total compressed bytes uploaded.</summary>
    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);
    private long _bytesUploaded;

    /// <summary>Current upload info (content hash or file name) for the in-flight upload.</summary>
    public volatile string? CurrentUploadFile;

    /// <summary>Total size of the currently uploading chunk.</summary>
    public long CurrentUploadFileSize => Interlocked.Read(ref _currentUploadFileSize);
    private long _currentUploadFileSize;

    /// <summary>Bytes uploaded so far for the current in-flight chunk.</summary>
    public long CurrentUploadBytesRead => Interlocked.Read(ref _currentUploadBytesRead);
    private long _currentUploadBytesRead;

    public void IncrementChunksUploading(string contentHash, long size)
    {
        Interlocked.Increment(ref _chunksUploading);
        CurrentUploadFile = contentHash;
        Interlocked.Exchange(ref _currentUploadFileSize, size);
        Interlocked.Exchange(ref _currentUploadBytesRead, 0);
    }

    public void IncrementChunksUploaded(long compressedSize)
    {
        Interlocked.Increment(ref _chunksUploaded);
        Interlocked.Decrement(ref _chunksUploading);
        Interlocked.Add(ref _bytesUploaded, compressedSize);
    }

    public void SetUploadProgress(long bytesRead) =>
        Interlocked.Exchange(ref _currentUploadBytesRead, bytesRead);

    // ── Archive: tar bundles ──────────────────────────────────────────────────

    /// <summary>Number of tar bundles sealed and ready for upload.</summary>
    public int TarsBundled => (int)Interlocked.Read(ref _tarsBundled);
    private long _tarsBundled;

    /// <summary>Number of tar bundles successfully uploaded.</summary>
    public int TarsUploaded => (int)Interlocked.Read(ref _tarsUploaded);
    private long _tarsUploaded;

    public void IncrementTarsBundled() => Interlocked.Increment(ref _tarsBundled);
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
