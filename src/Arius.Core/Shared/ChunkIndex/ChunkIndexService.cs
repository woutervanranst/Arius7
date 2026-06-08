using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Disk-backed chunk index cache.
/// </summary>
[SharedWithinAssembly]
internal sealed class ChunkIndexService : IChunkIndexService
{
    internal const           int          ShardPrefixLength          = 2;
    internal const           int          FlushWorkers               = 1; // TODO 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    private readonly IBlobContainerService                            _blobs;
    private readonly IEncryptionService                               _encryption;
    private readonly RelativeFileSystem                               _repositoryFileSystem;
    private readonly ChunkIndexLocalStore                             _localStore;
    private readonly ConcurrentDictionary<PathSegment, SemaphoreSlim> _prefixGates = [];
    private readonly AsyncLazy<string>                                _latestSnapshotName;
    private          int                                              _acceptingEntries = 1;

    // -- Construction --------------------------------------------------------

    /// <summary>
    /// Creates a chunk-index service for one repository and its local cache state.
    /// </summary>
    /// <param name="blobs">Blob storage used for chunk-index shard reads and writes.</param>
    /// <param name="encryption">Encryption used when serializing and deserializing shards.</param>
    /// <param name="snapshotService">Snapshot service used to validate local cache state against the latest snapshot.</param>
    /// <param name="accountName">Storage account name used to derive local repository state paths.</param>
    /// <param name="containerName">Container name used to derive local repository state paths.</param>
    public ChunkIndexService(
        IBlobContainerService blobs,
        IEncryptionService encryption,
        ISnapshotService snapshotService,
        string accountName,
        string containerName,
        ILoggerFactory? loggerFactory = null)
    {
        _blobs      = blobs;
        _encryption = encryption;

        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName);
        _repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _localStore = new ChunkIndexLocalStore(cacheRoot, loggerFactory?.CreateLogger<ChunkIndexLocalStore>());
        _latestSnapshotName = new AsyncLazy<string>(async () =>
        {
            var snapshots = await snapshotService.ListBlobNamesAsync();
            return snapshots.Count == 0
                ? "<none>"
                : snapshots[^1].Name.ToString();
        });
    }

    // -- Lookup --------------------------------------------------------------

    /// <summary>
    /// Resolves chunk-index entries for the specified content hashes.
    /// </summary>
    /// <param name="contentHashes">Content hashes to resolve.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>A dictionary containing the entries that exist for the requested content hashes.</returns>
    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        // NOTE: this method needs to be battle tested during list/restore optimization

        var hashes = contentHashes.Distinct().ToArray();
        var result = new Dictionary<ContentHash, ShardEntry>(hashes.Length);
        if (hashes.Length == 0)
            return result;

        var validationWork = new List<(PathSegment Prefix, List<ContentHash> Hashes)>();
        foreach (var prefixGroup in hashes.GroupBy(ChunkIndexRouter.GetLeafPrefix))
        {
            var hashesNeedingValidation = new List<ContentHash>();
            foreach (var contentHash in prefixGroup)
            {
                var pendingFlushEntry = _localStore.FindPendingFlushEntry(contentHash);
                if (pendingFlushEntry is not null)
                {
                    result[contentHash] = pendingFlushEntry;
                    continue;
                }

                hashesNeedingValidation.Add(contentHash);
            }

            if (hashesNeedingValidation.Count == 0)
                continue;

            validationWork.Add((prefixGroup.Key, hashesNeedingValidation));
        }

        if (validationWork.Count == 0)
            return result;

        var latestSnapshotName = await _latestSnapshotName;
        foreach (var item in validationWork)
        {
            await EnsurePrefixLoadedAndSynchronizedAsync(item.Prefix, latestSnapshotName, cancellationToken);
            foreach (var contentHash in item.Hashes)
            {
                var entry = _localStore.FindEntry(contentHash);
                if (entry is not null)
                    result[contentHash] = entry;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the chunk-index entry for a single content hash.
    /// </summary>
    /// <param name="contentHash">The content hash to resolve.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>The matching entry when present; otherwise <see langword="null"/>.</returns>
    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        var pendingFlushEntry = _localStore.FindPendingFlushEntry(contentHash);
        if (pendingFlushEntry is not null)
            return pendingFlushEntry;

        var latestSnapshot = await _latestSnapshotName;
        await EnsurePrefixLoadedAndSynchronizedAsync(ChunkIndexRouter.GetLeafPrefix(contentHash), latestSnapshot, cancellationToken);
        return _localStore.FindEntry(contentHash);
    }

    /// <summary>
    /// Promotes all loaded prefixes validated against the current snapshot version to the specified snapshot version.
    /// </summary>
    public async Task PromoteToSnapshotVersionAsync(string newSnapshotVersion)
    {
        ThrowIfRepairIncomplete();

        var oldSnapshotVersion = await _latestSnapshotName;
        if (StringComparer.Ordinal.Equals(oldSnapshotVersion, newSnapshotVersion))
            return;

        _localStore.PromoteToSnapshotVersion(oldSnapshotVersion, newSnapshotVersion);
    }

    // -- Synchronization -----------------------------------------------------

    /// <summary>
    /// Ensures the specified prefix is loaded into the local cache and recorded against the latest snapshot version.
    /// </summary>
    /// <param name="prefix">The shard prefix to synchronize.</param>
    /// <param name="latestSnapshotVersion">The snapshot version that the local cache must be recorded against.</param>
    /// <param name="cancellationToken">Cancellation token for the synchronization.</param>
    private async Task EnsurePrefixLoadedAndSynchronizedAsync(PathSegment prefix, string latestSnapshotVersion, CancellationToken cancellationToken)
    {
        var gate = _prefixGates.GetOrAdd(prefix, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // is the local version up to date with the snapshot. if it is, we dont need to make a remote call to check the etag
            if (_localStore.IsPrefixAtSnapshotVersion(prefix, latestSnapshotVersion))
                return; //Path 1

            // let's check remote
            var blobName = BlobPaths.ChunkIndexShardPath(prefix);
            var remoteShard = await _blobs.TryDownloadAsync(blobName, cancellationToken);
            if (remoteShard is null)
            {
                // it doesnt exist on remote -> it s a new shard
                _localStore.AddEmptyPrefix(prefix, latestSnapshotVersion);
                return; //Path 2
            }

            await using var stream = remoteShard.Stream; // ensure the stream is disposed if we go through path 3a

            // it exists at remote - is remote on the same version?
            if (_localStore.IsPrefixAtETag(prefix, remoteShard.ETag))
            {
                // our local copy is on the same version, update the snapshot version
                _localStore.SetPrefixSnapshotVersion(prefix, remoteShard.ETag, latestSnapshotVersion);
                return; //Path 3a
            }

            // remote has a more recent version - Path 3b
            Shard shard;
            try
            {
                shard = ShardSerializer.Deserialize(stream, _encryption);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
            {
                throw new ChunkIndexCorruptException(blobName, ex);
            }

            _localStore.UpdatePrefix(prefix, remoteShard.ETag, latestSnapshotVersion, shard.Entries);
        }
        finally
        {
            gate.Release();
        }
    }

    // -- AddEntry ------------------------------------------------------------

    /// <summary>
    /// Records a newly discovered or uploaded chunk-index entry as pending local flush state.
    /// </summary>
    /// <param name="entry">The entry to record.</param>
    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        _localStore.UpsertPendingFlush(entry);
    }

    // -- Flush & Upload ---------------------------------------------------------------

    /// <summary>
    /// Uploads pending local shard state and marks the flushed prefixes as synchronized remote-backed cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the flush.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (Interlocked.Exchange(ref _acceptingEntries, 0) == 0)
            throw new InvalidOperationException("Chunk-index service cannot be used after flush has started.");

        var prefixesWithPendingFlushes = _localStore.GetPrefixesWithPendingFlushes();
        if (prefixesWithPendingFlushes.Count == 0)
            return; // no shards need to be written to blob

        var latestSnapshotVersion = await _latestSnapshotName;
        var uploadedStates = new ConcurrentDictionary<PathSegment, string>();

        await Parallel.ForEachAsync(
            prefixesWithPendingFlushes,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (prefix, ct) =>
            {
                // 1. Ensure the local copy of the shard is in sync with remote - the local cache may be out of date from a previous run on another machine
                await EnsurePrefixLoadedAndSynchronizedAsync(prefix, latestSnapshotVersion, ct);

                var shard = BuildShard(prefix);
                if (shard.Count == 0)
                    return;

                var result = await UploadShardAsync(prefix, shard, ct);
                uploadedStates[prefix] = result.ETag;
            });

        _localStore.MarkPendingFlushesSynchronized(uploadedStates.Select(x => (x.Key, x.Value)), latestSnapshotVersion);
    }

    /// <summary>
    /// Builds the current shard payload for one prefix from local store state.
    /// </summary>
    /// <param name="prefix">The shard prefix to materialize.</param>
    /// <returns>A shard containing all currently stored entries for the prefix.</returns>
    private Shard BuildShard(PathSegment prefix)
    {
        var shard = new Shard();
        _localStore.ReadPrefixEntries(prefix, shard.AddOrUpdate);
        return shard;
    }

    /// <summary>
    /// Serializes and uploads a shard to its remote chunk-index blob.
    /// </summary>
    /// <param name="prefix">The shard prefix being uploaded.</param>
    /// <param name="shard">The shard payload to upload.</param>
    /// <param name="cancellationToken">Cancellation token for the upload.</param>
    /// <returns>The upload result returned by blob storage.</returns>
    private async Task<UploadResult> UploadShardAsync(PathSegment prefix, Shard shard, CancellationToken cancellationToken)
    {
        var bytes = await ShardSerializer.SerializeAsync(shard, _encryption, cancellationToken);
        return await _blobs.UploadAsync(
            BlobPaths.ChunkIndexShardPath(prefix),
            new MemoryStream(bytes),
            new Dictionary<string, string>(),
            BlobTier.Cool,
            _encryption.IsEncrypted ? ContentTypes.ChunkIndexGcmEncrypted : ContentTypes.ChunkIndexPlaintext,
            overwrite: true,
            cancellationToken: cancellationToken);
    }

    // -- Cache ---------------------------------------------------------------

    /// <summary>
    /// Drops the remote-backed local cache so later lookups revalidate prefixes from remote state.
    /// </summary>
    public void InvalidateCaches()
    {
        ThrowIfFlushed();
        _localStore.ClearRemoteBackedCache();
    }

    // -- Repair --------------------------------------------------------------

    /// <summary>
    /// Rebuilds the chunk index from chunk blobs and republishes the shard set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the repair.</param>
    /// <returns>A summary of the repair work that was performed.</returns>
    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFlushed();
        AddRepairMarker();
        _localStore.RecreateDatabase(backupExisting: true);

        var listedChunkCount = 0;
        var rebuiltEntryCount = 0;

        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            listedChunkCount++;

            var entry = CreateRepairEntry(item);
            if (entry is null)
                continue;

            _localStore.UpsertRemoteBacked(entry);
            rebuiltEntryCount++;
        }

        var rebuiltPrefixes = _localStore.GetStoredPrefixes().ToHashSet();

        var uploadedShardCount = 0;
        await Parallel.ForEachAsync(
            rebuiltPrefixes,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (prefix, ct) =>
            {
                var shard = BuildShard(prefix);
                if (shard.Count == 0)
                    return;

                await UploadShardAsync(prefix, shard, ct);
                Interlocked.Increment(ref uploadedShardCount);
            });

        // Delete stale shards
        var deletedStaleShardCount = 0;
        await Parallel.ForEachAsync(
            _blobs.ListAsync(BlobPaths.ChunkIndexPrefix, cancellationToken: cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                if (rebuiltPrefixes.Contains(item.Name.Name))
                    return;

                await _blobs.DeleteAsync(item.Name, ct);

                Interlocked.Increment(ref deletedStaleShardCount);
            });

        DeleteRepairMarker();

        return new ChunkIndexRepairResult(listedChunkCount, rebuiltEntryCount, rebuiltPrefixes.Count, uploadedShardCount, deletedStaleShardCount);
    }

    /// <summary>
    /// Converts a chunk blob listing item into a rebuildable shard entry when that blob contributes to the chunk index.
    /// </summary>
    /// <param name="item">The listed chunk blob.</param>
    /// <returns>The rebuilt shard entry, or <see langword="null"/> when the blob should not appear in the chunk index.</returns>
    private static ShardEntry? CreateRepairEntry(BlobListItem item)
    {
        var metadata = item.Metadata ?? throw new ChunkIndexRepairException(item.Name, "metadata was not loaded for repair listing");
        if (!metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType))
            return null;

        return ariusType switch
        {
            BlobMetadataKeys.TypeLarge => CreateLargeRepairEntry(item),
            BlobMetadataKeys.TypeThin  => CreateThinRepairEntry(item),
            BlobMetadataKeys.TypeTar   => null, // TAR entries will be recovered by the thin chunks
            _                          => null,
        };

        static ShardEntry CreateLargeRepairEntry(BlobListItem item)
        {
            var contentHash    = ContentHash.Parse(item.Name.Name.ToString());
            var originalSize   = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
            var compressedSize = ReadChunkSize(item);
            return new ShardEntry(contentHash, ChunkHash.Parse(contentHash), originalSize, compressedSize);
        }

        static ShardEntry CreateThinRepairEntry(BlobListItem item)
        {
            var contentHash = ContentHash.Parse(item.Name.Name.ToString());
            if (!item.Metadata!.TryGetValue(BlobMetadataKeys.ParentChunkHash, out var parentChunkHashValue) || !ChunkHash.TryParse(parentChunkHashValue, out var parentChunkHash))
                throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ParentChunkHash} metadata");

            var originalSize   = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
            var compressedSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.CompressedSize);
            return new ShardEntry(contentHash, parentChunkHash, originalSize, compressedSize);
        }

        static long ReadChunkSize(BlobListItem item)
            => item.Metadata is not null && item.Metadata.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
                ? size
                : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");

        static long ReadRequiredLongMetadata(BlobListItem item, string key)
            => item.Metadata is not null && item.Metadata.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
                ? parsed
                : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");
    }

    // -- Local Repair Marker -------------------------------------------------

    /// <summary>
    /// Fails fast when a previous repair did not complete and the local index cannot be trusted.
    /// </summary>
    private void ThrowIfRepairIncomplete()
    {
        if (IsRepairMarker())
            throw new ChunkIndexRepairIncompleteException();
    }

    private void ThrowIfFlushed()
    {
        if (Volatile.Read(ref _acceptingEntries) == 0)
            throw new InvalidOperationException("Chunk-index service cannot be used after flush has started.");
    }

    private bool IsRepairMarker()     => _repositoryFileSystem.FileExists(RepairInProgressMarkerPath);
    private void AddRepairMarker()    => _repositoryFileSystem.WriteAllBytes(RepairInProgressMarkerPath, []);
    private void DeleteRepairMarker() => _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);

    // -- Lifetime ------------------------------------------------------------

    /// <summary>
    /// Disposes the local chunk-index store.
    /// </summary>
    public void Dispose()
    {
    }
}
