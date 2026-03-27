using System.Collections.Concurrent;

namespace Arius.Cli;

/// <summary>
/// Per-file progress for an in-flight hash or upload operation.
/// <see cref="BytesProcessed"/> is updated via <see cref="Interlocked.Exchange(ref long, long)"/>
/// for lock-free thread safety.
/// </summary>
public sealed class FileProgress
{
    public FileProgress(string fileName, long totalBytes)
    {
        FileName   = fileName;
        TotalBytes = totalBytes;
    }

    public string FileName   { get; }
    public long   TotalBytes { get; }

    private long _bytesProcessed;

    /// <summary>Cumulative bytes processed so far.</summary>
    public long BytesProcessed => Interlocked.Read(ref _bytesProcessed);

    /// <summary>Updates <see cref="BytesProcessed"/> to <paramref name="value"/>.</summary>
    public void SetBytesProcessed(long value) =>
        Interlocked.Exchange(ref _bytesProcessed, value);
}

/// <summary>
/// Shared progress state for archive and restore operations.
/// All counter updates use <see cref="Interlocked"/> operations or concurrent collections
/// for thread safety.
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

    /// <summary>Files currently being hashed, keyed by relative path.</summary>
    public ConcurrentDictionary<string, FileProgress> InFlightHashes { get; } = new();

    /// <summary>Number of files currently being hashed (in-flight).</summary>
    public int FilesHashing => InFlightHashes.Count;

    /// <summary>Number of files for which hashing has completed.</summary>
    public long FilesHashed => Interlocked.Read(ref _filesHashed);
    private long _filesHashed;

    public void IncrementFilesHashing(string relativePath, long fileSize) =>
        InFlightHashes.TryAdd(relativePath, new FileProgress(relativePath, fileSize));

    public void IncrementFilesHashed(string relativePath)
    {
        InFlightHashes.TryRemove(relativePath, out _);
        Interlocked.Increment(ref _filesHashed);
    }

    // ── Archive: uploading ────────────────────────────────────────────────────

    /// <summary>Chunks currently being uploaded, keyed by content hash.</summary>
    public ConcurrentDictionary<string, FileProgress> InFlightUploads { get; } = new();

    /// <summary>Number of chunks currently being uploaded (in-flight).</summary>
    public int ChunksUploading => InFlightUploads.Count;

    /// <summary>Total chunks successfully uploaded.</summary>
    public long ChunksUploaded => Interlocked.Read(ref _chunksUploaded);
    private long _chunksUploaded;

    /// <summary>Total compressed bytes uploaded.</summary>
    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);
    private long _bytesUploaded;

    /// <summary>
    /// Total number of chunks to upload (known after dedup completes).
    /// <c>null</c> while dedup is still in progress — used for indeterminate→determinate transition.
    /// </summary>
    public long? TotalChunks => _totalChunks < 0 ? null : _totalChunks;
    private long _totalChunks = -1;

    public void IncrementChunksUploading(string contentHash, long size) =>
        InFlightUploads.TryAdd(contentHash, new FileProgress(contentHash, size));

    public void IncrementChunksUploaded(string contentHash, long compressedSize)
    {
        InFlightUploads.TryRemove(contentHash, out _);
        Interlocked.Increment(ref _chunksUploaded);
        Interlocked.Add(ref _bytesUploaded, compressedSize);
    }

    public void SetTotalChunks(long count) => Interlocked.Exchange(ref _totalChunks, count);

    // ── Archive: tar bundles ──────────────────────────────────────────────────

    /// <summary>Number of entries in the current (unsealed) tar bundle.</summary>
    public int CurrentTarEntryCount => (int)Interlocked.Read(ref _currentTarEntryCount);
    private long _currentTarEntryCount;

    /// <summary>Cumulative uncompressed size of the current tar bundle in bytes.</summary>
    public long CurrentTarSize => Interlocked.Read(ref _currentTarSize);
    private long _currentTarSize;

    /// <summary>Number of tar bundles sealed and ready for upload.</summary>
    public int TarsBundled => (int)Interlocked.Read(ref _tarsBundled);
    private long _tarsBundled;

    /// <summary>Number of tar bundles successfully uploaded.</summary>
    public int TarsUploaded => (int)Interlocked.Read(ref _tarsUploaded);
    private long _tarsUploaded;

    public void UpdateTarEntry(int currentEntryCount, long currentTarSize)
    {
        Interlocked.Exchange(ref _currentTarEntryCount, currentEntryCount);
        Interlocked.Exchange(ref _currentTarSize,       currentTarSize);
    }

    public void SealTar()
    {
        Interlocked.Exchange(ref _currentTarEntryCount, 0);
        Interlocked.Exchange(ref _currentTarSize,       0);
        Interlocked.Increment(ref _tarsBundled);
    }

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
