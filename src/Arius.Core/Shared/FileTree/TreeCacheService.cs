using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Two-tier cache for filetree blobs (disk → Azure).
///
/// Disk cache directory: <c>~/.arius/{accountName}-{containerName}/filetrees/</c>
/// Snapshot markers:     <c>~/.arius/{accountName}-{containerName}/snapshots/</c>
///
/// Cache strategy:
/// <list type="bullet">
///   <item>Filetree blobs are immutable (content-addressed). A cached file is never stale.</item>
///   <item><see cref="ValidateAsync"/> compares the latest local snapshot marker with the latest
///     remote snapshot. On mismatch, it lists all remote <c>filetrees/</c> blobs and materializes
///     an empty marker file on disk for each one not already cached, so that
///     <see cref="ExistsInRemote"/> is always a <c>File.Exists</c> check on both fast and slow paths.</item>
///   <item>On snapshot mismatch the chunk-index L2 directory is also deleted so
///     <see cref="ChunkIndexService"/> is forced to re-download stale shards.</item>
/// </list>
/// </summary>
public sealed class TreeCacheService
{
    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly string                _diskCacheDir;
    private readonly string                _snapshotsDir;
    private readonly string                _chunkIndexL2Dir;

    /// <summary>
    /// Guard ensuring <see cref="ExistsInRemote"/> is not called before <see cref="ValidateAsync"/>.
    /// </summary>
    private bool _validated;

    /// <param name="blobs">Blob storage backend.</param>
    /// <param name="encryption">Encryption/hashing service.</param>
    /// <param name="accountName">Used to derive the local cache directory.</param>
    /// <param name="containerName">Used to derive the local cache directory.</param>
    public TreeCacheService(
        IBlobContainerService blobs,
        IEncryptionService    encryption,
        string                accountName,
        string                containerName)
    {
        _blobs           = blobs;
        _encryption      = encryption;
        _diskCacheDir    = GetDiskCacheDirectory(accountName, containerName);
        _snapshotsDir    = GetSnapshotsDirectory(accountName, containerName);
        _chunkIndexL2Dir = ChunkIndexService.GetL2Directory(accountName, containerName);

        Directory.CreateDirectory(_diskCacheDir);
        Directory.CreateDirectory(_snapshotsDir);
    }

    // ── Directory helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>~/.arius/{accountName}-{containerName}/filetrees</c>.
    /// </summary>
    public static string GetDiskCacheDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".arius", ChunkIndexService.GetRepoDirectoryName(accountName, containerName), "filetrees");
    }

    /// <summary>
    /// Returns <c>~/.arius/{accountName}-{containerName}/snapshots</c>.
    /// </summary>
    public static string GetSnapshotsDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".arius", ChunkIndexService.GetRepoDirectoryName(accountName, containerName), "snapshots");
    }

    // ── 1.3 ReadAsync ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="TreeBlob"/> for the given <paramref name="hash"/>.
    /// <list type="bullet">
    ///   <item>Cache hit: reads the plaintext disk file and deserializes.</item>
    ///   <item>Cache miss: downloads from Azure, writes plaintext to disk, returns blob.</item>
    /// </list>
    /// </summary>
    public async Task<TreeBlob> ReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        var diskPath = Path.Combine(_diskCacheDir, hash);

        // Disk hit (may be a real file or an empty marker; empty ⇒ treat as miss)
        if (File.Exists(diskPath))
        {
            var cached = await File.ReadAllBytesAsync(diskPath, cancellationToken);
            if (cached.Length > 0)
                return TreeBlobSerializer.Deserialize(cached);
        }

        // Azure fallback
        var blobName = BlobPaths.FileTree(hash);
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption, cancellationToken);

        // Write plaintext to disk (fills in any empty marker file)
        var plaintext = TreeBlobSerializer.Serialize(treeBlob);
        await File.WriteAllBytesAsync(diskPath, plaintext, cancellationToken);

        return treeBlob;
    }

    // ── 1.4 WriteAsync ────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads the tree blob to Azure (if not already present) and writes the plaintext
    /// representation to the local disk cache.
    /// </summary>
    public async Task WriteAsync(string hash, TreeBlob tree, CancellationToken cancellationToken = default)
    {
        var blobName     = BlobPaths.FileTree(hash);
        var storageBytes = await TreeBlobSerializer.SerializeForStorageAsync(tree, _encryption, cancellationToken);
        var contentType  = _encryption.IsEncrypted
            ? ContentTypes.FileTreeGcmEncrypted
            : ContentTypes.FileTreePlaintext;

        try
        {
            await _blobs.UploadAsync(
                blobName,
                new MemoryStream(storageBytes),
                new Dictionary<string, string>(),
                BlobTier.Cool,
                contentType,
                overwrite: false,
                cancellationToken: cancellationToken);
        }
        catch (BlobAlreadyExistsException)
        {
            // Blob was already uploaded (crash recovery or concurrent run). Continue.
        }

        // Write plaintext to disk cache regardless of whether upload was new or existing.
        var diskPath  = Path.Combine(_diskCacheDir, hash);
        var plaintext = TreeBlobSerializer.Serialize(tree);
        await File.WriteAllBytesAsync(diskPath, plaintext, cancellationToken);
    }

    // ── 1.5 ValidateAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Compares the latest local snapshot marker with the latest remote snapshot.
    /// Idempotent: calling it again after validation has already completed is a no-op.
    ///
    /// <b>Fast path</b> (match): the disk cache is trusted; no listing performed.
    ///
    /// <b>Slow path</b> (mismatch or no local markers):
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
        if (_validated) return;
        // Latest local snapshot marker (lexicographic sort = timestamp sort because of the format)
        var latestLocal = Directory.EnumerateFiles(_snapshotsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .FirstOrDefault();

        // Latest remote snapshot
        string? latestRemote = null;
        await foreach (var name in _blobs.ListAsync(BlobPaths.Snapshots, cancellationToken))
        {
            // ListAsync returns names sorted; last one is newest
            latestRemote = Path.GetFileName(name); // strip "snapshots/" prefix
        }

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

        // Slow path: snapshot mismatch (or no local marker at all).
        // Materialize empty marker files for all remote filetree blobs not yet cached.
        await foreach (var blobName in _blobs.ListAsync(BlobPaths.FileTrees, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash     = Path.GetFileName(blobName); // strip "filetrees/" prefix
            var diskPath = Path.Combine(_diskCacheDir, hash);
            if (!File.Exists(diskPath))
            {
                // Create an empty marker file (will be filled by ReadAsync on demand)
                await File.WriteAllBytesAsync(diskPath, [], cancellationToken);
            }
        }

        // Invalidate chunk-index L2 (another machine may have updated shards)
        if (Directory.Exists(_chunkIndexL2Dir))
        {
            foreach (var file in Directory.EnumerateFiles(_chunkIndexL2Dir))
            {
                try { File.Delete(file); } catch { /* ignore individual failures */ }
            }
        }

        _validated = true;
    }

    // ── 1.6 ExistsInRemote ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the filetree blob for the given <paramref name="hash"/> exists in
    /// the remote (or is already cached locally). After <see cref="ValidateAsync"/> has run, this
    /// is always a plain <see cref="File.Exists"/> check — empty marker files represent remote
    /// blobs that have not yet been fully downloaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="ValidateAsync"/>.</exception>
    public bool ExistsInRemote(string hash)
    {
        if (!_validated)
            throw new InvalidOperationException(
                $"{nameof(ExistsInRemote)} must not be called before {nameof(ValidateAsync)}.");

        return File.Exists(Path.Combine(_diskCacheDir, hash));
    }

    // ── 1.7 WriteSnapshotMarkerAsync ──────────────────────────────────────────

    /// <summary>
    /// Creates an empty marker file at <c>~/.arius/{repo}/snapshots/{timestamp}</c>.
    /// Called after <see cref="SnapshotService.CreateAsync"/> succeeds.
    /// </summary>
    public async Task WriteSnapshotMarkerAsync(string timestamp, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_snapshotsDir);
        var path = Path.Combine(_snapshotsDir, timestamp);
        await File.WriteAllBytesAsync(path, [], cancellationToken);
    }
}
