using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Disk-backed chunk index cache.
/// </summary>
public sealed class ChunkIndexService : IDisposable
{
    internal const           int          ShardPrefixLength          = 2;
    internal const           int          FlushWorkers               = 1; // TODO 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    private readonly IBlobContainerService                            _blobs;
    private readonly IEncryptionService                               _encryption;
    private readonly RelativeFileSystem                               _repositoryFileSystem;
    private readonly ChunkIndexLocalStore                             _localStore;
    private readonly ConcurrentDictionary<PathSegment, SemaphoreSlim> _prefixGates = [];
    private readonly AsyncLazy<string>                                _latestSnapshot;
    private          int                                              _flushInProgress;

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
        _latestSnapshot = new AsyncLazy<string>(async () =>
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
                var dirtyEntry = _localStore.FindDirtyEntry(contentHash);
                if (dirtyEntry is not null)
                {
                    result[contentHash] = dirtyEntry;
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

        var latestSnapshotIdentity = await _latestSnapshot;
        foreach (var item in validationWork)
        {
            await EnsurePrefixLoadedAndValidatedAsync(item.Prefix, latestSnapshotIdentity, cancellationToken);
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

        var dirtyEntry = _localStore.FindDirtyEntry(contentHash);
        if (dirtyEntry is not null)
            return dirtyEntry;

        var latestSnapshot = await _latestSnapshot;
        await EnsurePrefixLoadedAndValidatedAsync(ChunkIndexRouter.GetLeafPrefix(contentHash), latestSnapshot, cancellationToken);
        return _localStore.FindEntry(contentHash);
    }

    // -- Synchronization -----------------------------------------------------

    /// <summary>
    /// Ensures the specified prefix is loaded into the local cache and validated against the latest snapshot identity.
    /// </summary>
    /// <param name="prefix">The shard prefix to synchronize.</param>
    /// <param name="latestSnapshotIdentity">The snapshot identity that the local cache must be validated against.</param>
    /// <param name="cancellationToken">Cancellation token for the synchronization.</param>
    private async Task EnsurePrefixLoadedAndValidatedAsync(PathSegment prefix, string latestSnapshotIdentity, CancellationToken cancellationToken)
    {
        var gate = _prefixGates.GetOrAdd(prefix, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // do we have a locally up to date version with the snapshot version
            var loadedPrefix = _localStore.GetLoadedPrefixState(prefix);
            if (loadedPrefix is not null && string.Equals(loadedPrefix.ValidatedSnapshotIdentity, latestSnapshotIdentity, StringComparison.Ordinal))
                return;

            // we need to get it from remote
            var blobName = BlobPaths.ChunkIndexShardPath(prefix);
            var download = await _blobs.TryDownloadAsync(blobName, cancellationToken);
            if (download is null)
            {
                // it doesnt exist on remote -> it s a new shard
                _localStore.ClearPrefix(new LoadedPrefixState(prefix, false, null, latestSnapshotIdentity));
                return;
            }

            // get it from remote
            await using var stream = download.Stream;
            if (loadedPrefix is not null && loadedPrefix.RemoteExists && string.Equals(loadedPrefix.RemoteBlobIdentity, download.BlobIdentity, StringComparison.Ordinal))
            {
                // our local copy was up to date
                _localStore.UpdateLoadedPrefixState(new LoadedPrefixState(prefix, true, download.BlobIdentity, latestSnapshotIdentity));
                return;
            }

            // remote has a more up to date version
            _localStore.DeleteCleanPrefix(prefix);
            Shard shard;
            try
            {
                shard = ShardSerializer.Deserialize(stream, _encryption);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
            {
                throw new ChunkIndexCorruptException(blobName, ex);
            }

            _localStore.IngestCleanPrefix(new LoadedPrefixState(prefix, true, download.BlobIdentity, latestSnapshotIdentity), shard.Entries);
        }
        finally
        {
            gate.Release();
        }
    }

    // -- AddEntry ------------------------------------------------------------

    /// <summary>
    /// Records a newly discovered or uploaded chunk-index entry as dirty local state.
    /// </summary>
    /// <param name="entry">The entry to record.</param>
    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot record chunk-index entries while a flush is in progress.");

        _localStore.UpsertDirty(entry);
    }

    // -- Flush ---------------------------------------------------------------

    /// <summary>
    /// Uploads dirty local shard state and marks the flushed prefixes as clean.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the flush.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (Interlocked.Exchange(ref _flushInProgress, 1) != 0)
            throw new InvalidOperationException("Chunk-index flush is already in progress.");

        try
        {
            var dirtyPrefixes = _localStore.GetDirtyPrefixes();
            if (dirtyPrefixes.Count == 0)
                return; // no shards need to be written to blob

            var latestSnapshot = await _latestSnapshot;
            var uploadedStates = new ConcurrentDictionary<PathSegment, LoadedPrefixState>();

            await Parallel.ForEachAsync(
                dirtyPrefixes,
                new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
                async (prefix, ct) =>
                {
                    // 1. Ensure the local copy of the shard is in sync with remote - the local cache may be out of date from a previous run on another machine
                    await EnsurePrefixLoadedAndValidatedAsync(prefix, latestSnapshot, ct);

                    var shard = BuildShard(prefix);
                    if (shard.Count == 0)
                        return;

                    var result = await UploadShardAsync(prefix, shard, ct);
                    uploadedStates[prefix] = new LoadedPrefixState(prefix, true, result.BlobIdentity, latestSnapshot);
                });

            _localStore.MarkDirtyPrefixesClean(dirtyPrefixes);
            foreach (var state in uploadedStates.Values)
                _localStore.UpdateLoadedPrefixState(state);
        }
        finally
        {
            Volatile.Write(ref _flushInProgress, 0);
        }
    }

    // -- Shard Serialization -------------------------------------------------

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
    /// Drops the clean local cache so later lookups revalidate prefixes from remote state.
    /// </summary>
    public void InvalidateCaches()
    {
        _localStore.ClearCleanCache();
    }

    // -- Repair --------------------------------------------------------------

    /// <summary>
    /// Rebuilds the chunk index from chunk blobs and republishes the shard set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the repair.</param>
    /// <returns>A summary of the repair work that was performed.</returns>
    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
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

            _localStore.UpsertClean(entry);
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

    private bool IsRepairMarker()     => _repositoryFileSystem.FileExists(RepairInProgressMarkerPath);
    private void AddRepairMarker()    => _repositoryFileSystem.WriteAllBytes(RepairInProgressMarkerPath, []);
    private void DeleteRepairMarker() => _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);

    // -- Lifetime ------------------------------------------------------------

    /// <summary>
    /// Disposes the local chunk-index store.
    /// </summary>
    public void Dispose()
    {
        _localStore.Dispose();
    }
}
