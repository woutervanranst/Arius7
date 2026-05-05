using System.Collections.Concurrent;
using System.IO.Compression;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Two-tier cache for filetree blobs (disk → Azure).
///
/// Disk cache directory: <c>~/.arius/{accountName}-{containerName}/filetrees/</c>
///
/// Cache strategy:
/// <list type="bullet">
///   <item>Filetree blobs are immutable (content-addressed). A cached file is never stale.</item>
///   <item><see cref="ValidateAsync"/> compares the latest local snapshot (via
///     <see cref="SnapshotService.GetDiskCacheDirectory"/>) with the latest remote snapshot.
///     On mismatch, it lists all remote <c>filetrees/</c> blobs and materializes an empty marker
///     file on disk for each one not already cached, so that <see cref="ExistsInRemote"/> is
///     always a <c>File.Exists</c> check on both fast and slow paths.</item>
///   <item>On snapshot mismatch the chunk-index L2 directory is also deleted so
///     <see cref="ChunkIndexService"/> is forced to re-download stale shards.</item>
/// </list>
/// </summary>
public sealed class FileTreeService
{
    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly ChunkIndexService     _chunkIndex;
    private readonly LocalRootPath         _diskCacheDir;
    private readonly LocalRootPath         _snapshotsDir;
    private readonly LocalRootPath         _chunkIndexL2Dir;

    /// <summary>
    /// Guard ensuring <see cref="ExistsInRemote"/> is not called before <see cref="ValidateAsync"/>.
    /// </summary>
    private bool _validated;

    private readonly ConcurrentDictionary<FileTreeHash, TaskCompletionSource<IReadOnlyList<FileTreeEntry>>> _inFlightReads = [];

    /// <param name="blobs">Blob storage backend.</param>
    /// <param name="encryption">Encryption/hashing service.</param>
    /// <param name="chunkIndex">Chunk index service — used to invalidate the L1 in-memory cache on snapshot mismatch.</param>
    /// <param name="accountName">Used to derive the local cache directory.</param>
    /// <param name="containerName">Used to derive the local cache directory.</param>
    public FileTreeService(
        IBlobContainerService blobs,
        IEncryptionService    encryption,
        ChunkIndexService     chunkIndex,
        string                accountName,
        string                containerName)
    {
        _blobs           = blobs;
        _encryption      = encryption;
        _chunkIndex      = chunkIndex;
        _diskCacheDir    = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        _snapshotsDir    = RepositoryPaths.GetSnapshotCacheDirectory(accountName, containerName);
        _chunkIndexL2Dir = RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName);

        _diskCacheDir.CreateDirectory();
        // Note: _snapshotsDir is created by SnapshotService; we only read it here.
    }

    // ── 1.3 ReadAsync ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the persisted filetree entries for the given <paramref name="hash"/>.
    /// <list type="bullet">
    ///   <item>Cache hit: reads the plaintext disk file and deserializes.</item>
    ///   <item>Cache miss: downloads from Azure, writes plaintext to disk, returns blob.</item>
    /// </list>
    /// </summary>
    public async Task<IReadOnlyList<FileTreeEntry>> ReadAsync(FileTreeHash hash, CancellationToken cancellationToken = default)
    {
        var hashText = hash.ToString();
        var diskPath = FileTreePaths.GetCachePath(_diskCacheDir, hashText);

        // Avoid race conditions between concurrent readers and writers for the same hash by
        // coordinating via a per-hash in-flight task.
        //
        // Keep cache hits lock-free: immutable filetrees can be served straight from disk.
        // The subtle part is cache population on a miss. Another concurrent caller may probe the
        // same path while the first caller is still publishing the cache file. If we expose a
        // partially written file, the second caller can fail deserialization, delete the file as
        // "corrupt", and trigger a redundant Azure download. The per-hash in-flight task plus
        // atomic temp-file publish below avoids that race without introducing a coarse lock.
        if (TryReadCachedTree(diskPath, cancellationToken) is { } cachedTree)
            return cachedTree;

        var pendingRead = new TaskCompletionSource<IReadOnlyList<FileTreeEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightRead = _inFlightReads.GetOrAdd(hash, pendingRead);

        if (ReferenceEquals(inFlightRead, pendingRead))
        {
            try
            {
                var treeBlob = await DownloadAndCacheAsync(hash, diskPath);
                inFlightRead.TrySetResult(treeBlob);
            }
            catch (Exception ex)
            {
                inFlightRead.TrySetException(ex);
            }
            finally
            {
                _inFlightReads.TryRemove(new KeyValuePair<FileTreeHash, TaskCompletionSource<IReadOnlyList<FileTreeEntry>>>(hash, inFlightRead));
            }
        }

        return await inFlightRead.Task.WaitAsync(cancellationToken);


        IReadOnlyList<FileTreeEntry>? TryReadCachedTree(string diskPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(diskPath))
                return null;

            try
            {
                var cached = File.ReadAllBytes(diskPath);
                if (cached.Length == 0)
                    return null;

                try
                {
                    return FileTreeSerializer.Deserialize(cached);
                }
                catch (Exception)
                {
                    // Corrupt cache file (e.g. partial write from a prior crash).
                    // Delete and fall through to Azure download.
                    try { File.Delete(diskPath); } catch { /* best-effort */ }
                }
            }
            catch (FileNotFoundException)
            {
                // Another concurrent caller may have replaced the cache file between Exists and read.
            }
            catch (DirectoryNotFoundException)
            {
                // Treat concurrent directory cleanup or creation races as a cache miss.
            }

            return null;
        }

        async Task<IReadOnlyList<FileTreeEntry>> DownloadAndCacheAsync(FileTreeHash hash, string diskPath)
        {
            var             blobName = BlobPaths.FileTree(hash);
            await using var stream   = await _blobs.DownloadAsync(blobName, CancellationToken.None);
            var             entries  = await DeserializeStorageAsync(stream, CancellationToken.None);

            var plaintext = FileTreeSerializer.Serialize(entries);
            await WriteCacheAtomicallyAsync(diskPath, plaintext, CancellationToken.None);

            return entries;
        }
    }

    // ── WriteAsync ────────────────────────────────────────────────────────

    internal async Task EnsureStoredAsync((FileTreeHash Hash, ReadOnlyMemory<byte> Plaintext) payload, CancellationToken cancellationToken = default)
    {
        if (!ExistsInRemote(payload.Hash))
            await WriteAsync(payload, cancellationToken);
    }

    /// <summary>
    /// Uploads the tree entries to Azure (if not already present) and writes the plaintext
    /// representation to the local disk cache.
    /// </summary>
    public async Task WriteAsync((FileTreeHash Hash, ReadOnlyMemory<byte> Plaintext) payload, CancellationToken cancellationToken = default)
    {
        var hashText     = payload.Hash.ToString();
        var blobName     = BlobPaths.FileTree(payload.Hash);
        var storageBytes = await SerializeStorageAsync(payload.Plaintext, cancellationToken);
        var contentType  = _encryption.IsEncrypted
            ? ContentTypes.FileTreeGcmEncrypted
            : ContentTypes.FileTreePlaintext;

        try
        {
            await _blobs.UploadAsync(blobName, new MemoryStream(storageBytes), new Dictionary<string, string>(), BlobTier.Cool, contentType, overwrite: false, cancellationToken: cancellationToken);
        }
        catch (BlobAlreadyExistsException)
        {
            // Blob was already uploaded (crash recovery or concurrent run). Continue.
        }

        // Write plaintext to disk cache regardless of whether upload was new or existing.
        var diskPath  = FileTreePaths.GetCachePath(_diskCacheDir, hashText);
        await WriteCacheAtomicallyAsync(diskPath, payload.Plaintext, cancellationToken);
    }


    // -- Serialize & Deserialize ---

    private async Task<byte[]> SerializeStorageAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var ms        = new MemoryStream();

        await using (var encStream = _encryption.WrapForEncryption(ms))
        await using (var gzipStream = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzipStream.WriteAsync(plaintext, cancellationToken);
        }

        return ms.ToArray();
    }

    private async Task<IReadOnlyList<FileTreeEntry>> DeserializeStorageAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var decStream  = _encryption.WrapForDecryption(source);
        await using var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var       ms         = new MemoryStream();
        await gzipStream.CopyToAsync(ms, cancellationToken);
        return FileTreeSerializer.Deserialize(ms.ToArray());
    }

    private static async Task WriteCacheAtomicallyAsync(string diskPath, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(diskPath)!, $".{Path.GetFileName(diskPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempPath, plaintext.ToArray(), cancellationToken);

            // Publish with an atomic replace/move so concurrent readers either see the old cache
            // file or the complete new one, never a truncated intermediate file.
            if (OperatingSystem.IsWindows() && File.Exists(diskPath))
                File.Replace(tempPath, diskPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, diskPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }
    }

    // ── ValidateAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Compares the latest local snapshot (from <see cref="SnapshotService"/>'s disk cache) with
    /// the latest remote snapshot. Idempotent: a second call after validation is a no-op.
    ///
    /// <b>Fast path</b> (match): the disk cache is trusted; no listing performed.
    ///
    /// <b>Slow path</b> (mismatch or no local snapshots):
    /// <list type="number">
    ///   <item>Lists all <c>filetrees/</c> blobs from Azure.</item>
    ///   <item>Creates an empty file on disk for each remote blob not already cached.</item>
    ///   <item>Deletes all files in the chunk-index L2 directory (forces re-download of stale shards).</item>
    /// </list>
    ///
    /// Must be called once before <see cref="ExistsInRemote"/>.
    /// </summary>
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_validated)
            return;

        // Latest local snapshot filename (lexicographic = chronological due to timestamp format)
        var latestLocal = _snapshotsDir.ExistsDirectory
            ? Directory.EnumerateFiles(_snapshotsDir.ToString())
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;

        // Latest remote snapshot (sort explicitly rather than relying on backend enumeration order)
        var remoteSnapshots = new List<string>();
        await foreach (var name in _blobs.ListAsync(BlobPaths.Snapshots, cancellationToken))
        {
            var fileName = Path.GetFileName(name);
            if (!string.IsNullOrEmpty(fileName))
                remoteSnapshots.Add(fileName);
        }

        var latestRemote = remoteSnapshots
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();

        // If there are no remote snapshots, the repository is empty — fast path.
        if (latestRemote is null)
        {
            _validated = true;
            return;
        }

        // Fast path: this machine wrote the last snapshot.
        if (latestLocal is not null &&
            string.Equals(latestLocal, latestRemote, StringComparison.Ordinal))
        {
            _validated = true;
            return;
        }

        // Slow path: snapshot mismatch (or no local snapshot at all).
        // Materialize empty marker files for all remote filetree blobs not yet cached.
        //   Filetree blobs are immutable and content-addressed, so one remote list gives us a
        //   stable set of known-existing hashes for this epoch. Materialize empty marker files for
        //   any uncached remote trees now so ExistsInRemote() can stay a cheap local File.Exists()
        //   check during the entire build instead of doing a remote existence probe per tree node.
        await foreach (var blobName in _blobs.ListAsync(BlobPaths.FileTrees, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash     = Path.GetFileName(blobName); // strip "filetrees/" prefix
            var diskPath = FileTreePaths.GetCachePath(_diskCacheDir, hash);
            if (!File.Exists(diskPath))
            {
                // Create an empty marker file (will be filled by ReadAsync on demand)
                await File.WriteAllBytesAsync(diskPath, [], cancellationToken);
            }
        }

        // Invalidate chunk-index L2 (another machine may have updated shards)
        if (_chunkIndexL2Dir.ExistsDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(_chunkIndexL2Dir.ToString()))
            {
                try { File.Delete(file); } catch { /* ignore individual failures */ }
            }
        }

        // Also clear the in-memory L1 cache so stale shard data is not served from memory.
        _chunkIndex.InvalidateL1();

        _validated = true;
    }

    // ── ExistsInRemote ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the filetree blob for the given <paramref name="hash"/> exists in
    /// the remote (or is already cached locally). After <see cref="ValidateAsync"/> has run, this
    /// is always a plain <see cref="File.Exists"/> check — empty marker files represent remote
    /// blobs that have not yet been fully downloaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="ValidateAsync"/>.</exception>
    public bool ExistsInRemote(FileTreeHash hash)
    {
        if (!_validated)
            throw new InvalidOperationException($"{nameof(ExistsInRemote)} must not be called before {nameof(ValidateAsync)}.");

        return File.Exists(FileTreePaths.GetCachePath(_diskCacheDir, hash));
    }
}
