using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

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

    public ChunkIndexService(
        IBlobContainerService blobs, 
        IEncryptionService encryption,
        ISnapshotService snapshotService,
        string accountName, 
        string containerName)
    {
        _blobs      = blobs;
        _encryption = encryption;

        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName);
        _repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _localStore = new ChunkIndexLocalStore(cacheRoot);
        _latestSnapshot = new AsyncLazy<string>(async () =>
        {
            var snapshots= await snapshotService.ListBlobNamesAsync();
            return snapshots.Count == 0
                ? "<none>"
                : snapshots[^1].Name.ToString();
        });
    }

    // ── Lookup ─────────────────────────────────────────

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

    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        var dirtyEntry = _localStore.FindDirtyEntry(contentHash);
        if (dirtyEntry is not null)
            return dirtyEntry;

        var latestSnapshotIdentity = await _latestSnapshot;
        await EnsurePrefixLoadedAndValidatedAsync(ChunkIndexRouter.GetLeafPrefix(contentHash), latestSnapshotIdentity, cancellationToken);
        return _localStore.FindEntry(contentHash);
    }

    // ── Record new entry ──────────────────────────────────────────────────────

    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot record chunk-index entries while a flush is in progress.");

        _localStore.UpsertDirty(entry);
    }

    // ── Flush (upload shards at end of run) ───────────────────────────────────

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

    public void Dispose()
    {
        _localStore.Dispose();
    }

    public void InvalidateCaches()
    {
        _localStore.ClearCleanCache();
    }

    // -- Repair

    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        await AddRepairMarker();
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

    private void ThrowIfRepairIncomplete()
    {
        if (IsRepairMarker())
            throw new ChunkIndexRepairIncompleteException();
    }

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
                _localStore.DeleteCleanPrefix(prefix);
                _localStore.UpdateLoadedPrefixState(new LoadedPrefixState(prefix, false, null, latestSnapshotIdentity));
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

    private Shard BuildShard(PathSegment prefix)
    {
        var shard = new Shard();
        _localStore.ReadPrefixEntries(prefix, shard.AddOrUpdate);
        return shard;
    }

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

    private       bool IsRepairMarker()     => _repositoryFileSystem.FileExists(RepairInProgressMarkerPath);
    private async Task AddRepairMarker()    => await _repositoryFileSystem.WriteAllBytesAsync(RepairInProgressMarkerPath, [], CancellationToken.None);
    private       void DeleteRepairMarker() => _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);
}
