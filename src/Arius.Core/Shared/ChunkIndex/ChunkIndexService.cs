using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Disk-backed chunk index cache.
/// </summary>
public sealed class ChunkIndexService : IDisposable
{
    internal const           int          ShardPrefixLength          = 2;
    internal const           int          FlushWorkers               = 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    private readonly IBlobContainerService                            _blobs;
    private readonly IEncryptionService                               _encryption;
    private readonly RelativeFileSystem                               _repositoryFileSystem;
    private readonly ChunkIndexLocalStore                             _localStore;
    private readonly ConcurrentDictionary<PathSegment, SemaphoreSlim> _prefixGates = [];
    private          int                                              _flushInProgress;

    public ChunkIndexService(
        IBlobContainerService blobs, 
        IEncryptionService encryption, 
        string accountName, 
        string containerName)
    {
        _blobs      = blobs;
        _encryption = encryption;
        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName);
        var l2Root         = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);

        _localStore     = new ChunkIndexLocalStore(l2Root);
    }

    // ── Lookup ─────────────────────────────────────────

    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        return await WithLocalStoreRecoveryAsync(async () =>
        {
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
                    var localRow = _localStore.GetRowOrDefault(contentHash);
                    if (localRow is { IsDirty: true })
                    {
                        result[contentHash] = localRow.Entry;
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

            var latestSnapshotIdentity = await GetLatestSnapshotIdentityAsync(cancellationToken);
            foreach (var item in validationWork)
            {
                await EnsurePrefixLoadedAndValidatedAsync(item.Prefix, latestSnapshotIdentity, cancellationToken);
                foreach (var contentHash in item.Hashes)
                {
                    var entry = _localStore.GetValueOrDefault(contentHash);
                    if (entry is not null)
                        result[contentHash] = entry;
                }
            }

            return (IReadOnlyDictionary<ContentHash, ShardEntry>)result;
        });
    }

    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        return await WithLocalStoreRecoveryAsync(async () =>
        {
            var localRow = _localStore.GetRowOrDefault(contentHash);
            if (localRow is { IsDirty: true })
                return localRow.Entry;

            var latestSnapshotIdentity = await GetLatestSnapshotIdentityAsync(cancellationToken);
            await EnsurePrefixLoadedAndValidatedAsync(ChunkIndexRouter.GetLeafPrefix(contentHash), latestSnapshotIdentity, cancellationToken);
            return _localStore.GetValueOrDefault(contentHash);
        });
    }

    // ── Record new entry ──────────────────────────────────────────────────────

    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot record chunk-index entries while a flush is in progress.");

        WithLocalStoreRecovery(() =>
        {
            _localStore.UpsertDirty(entry);
        });
    }

    // ── Flush (upload shards at end of run) ───────────────────────────────────

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (Interlocked.Exchange(ref _flushInProgress, 1) != 0)
            throw new InvalidOperationException("Chunk-index flush is already in progress.");

        try
        {
            await WithLocalStoreRecoveryAsync(async () =>
            {
                var dirtyPrefixes = _localStore.GetDirtyPrefixes();
                if (dirtyPrefixes.Count == 0)
                    return;

                var latestSnapshotIdentity = await GetLatestSnapshotIdentityAsync(cancellationToken);
                var uploadedStates = new ConcurrentDictionary<PathSegment, LoadedPrefixState>();

                await Parallel.ForEachAsync(
                    dirtyPrefixes,
                    new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
                    async (prefix, ct) =>
                    {
                        await EnsurePrefixLoadedAndValidatedAsync(prefix, latestSnapshotIdentity, ct);
                        var shard = BuildShard(prefix);
                        if (shard.Count == 0)
                            return;

                        var metadata = await UploadShardAsync(prefix, shard, ct);
                        uploadedStates[prefix] = new LoadedPrefixState(prefix, true, metadata.BlobIdentity, latestSnapshotIdentity);
                    });

                _localStore.MarkDirtyPrefixesClean(dirtyPrefixes);
                foreach (var state in uploadedStates.Values)
                    _localStore.UpdateLoadedPrefixState(state);
            });
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
        WithLocalStoreRecovery(() => _localStore.ClearCleanCache());
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
    }

    private static ShardEntry CreateLargeRepairEntry(BlobListItem item)
    {
        var contentHash = ContentHash.Parse(item.Name.Name.ToString());
        var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
        var compressedSize = ReadChunkSize(item);
        return new ShardEntry(contentHash, ChunkHash.Parse(contentHash), originalSize, compressedSize);
    }

    private static ShardEntry CreateThinRepairEntry(BlobListItem item)
    {
        var contentHash = ContentHash.Parse(item.Name.Name.ToString());
        if (!item.Metadata!.TryGetValue(BlobMetadataKeys.ParentChunkHash, out var parentChunkHashValue) || !ChunkHash.TryParse(parentChunkHashValue, out var parentChunkHash))
            throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ParentChunkHash} metadata");

        var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
        var compressedSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.CompressedSize);
        return new ShardEntry(contentHash, parentChunkHash, originalSize, compressedSize);
    }

    private static long ReadChunkSize(BlobListItem item)
        => item.Metadata is not null && item.Metadata.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
            ? size
            : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");

    private static long ReadRequiredLongMetadata(BlobListItem item, string key)
        => item.Metadata is not null && item.Metadata.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
            ? parsed
            : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");

    private void ThrowIfRepairIncomplete()
    {
        if (IsRepairMarker())
            throw new ChunkIndexRepairIncompleteException();
    }

    private async Task<string> GetLatestSnapshotIdentityAsync(CancellationToken cancellationToken)
    {
        PathSegment? latest = null;
        await foreach (var item in _blobs.ListAsync(BlobPaths.SnapshotsPrefix, cancellationToken: cancellationToken))
        {
            var current = item.Name.Name;
            if (latest is null || current.Compare(latest.Value, StringComparer.Ordinal) > 0)
                latest = current;
        }

        return latest?.ToString() ?? "<none>";
    }

    private async Task EnsurePrefixLoadedAndValidatedAsync(PathSegment prefix, string latestSnapshotIdentity, CancellationToken cancellationToken)
    {
        var gate = _prefixGates.GetOrAdd(prefix, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var loadedPrefix = _localStore.GetLoadedPrefixState(prefix);
            if (loadedPrefix is not null && string.Equals(loadedPrefix.ValidatedSnapshotIdentity, latestSnapshotIdentity, StringComparison.Ordinal))
                return;

            var blobName = BlobPaths.ChunkIndexShardPath(prefix);
            var remoteMetadata = await _blobs.GetMetadataAsync(blobName, cancellationToken);
            if (loadedPrefix is not null &&
                loadedPrefix.RemoteExists == remoteMetadata.Exists &&
                string.Equals(loadedPrefix.RemoteBlobIdentity, remoteMetadata.BlobIdentity, StringComparison.Ordinal))
            {
                _localStore.UpdateLoadedPrefixState(new LoadedPrefixState(prefix, remoteMetadata.Exists, remoteMetadata.BlobIdentity, latestSnapshotIdentity));
                return;
            }

            _localStore.DeleteCleanPrefix(prefix);
            if (!remoteMetadata.Exists)
            {
                _localStore.IngestCleanPrefix(new LoadedPrefixState(prefix, false, null, latestSnapshotIdentity), []);
                return;
            }

            await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
            Shard shard;
            try
            {
                shard = ShardSerializer.Deserialize(stream, _encryption);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
            {
                throw new ChunkIndexCorruptException(blobName, ex);
            }

            _localStore.IngestCleanPrefix(new LoadedPrefixState(prefix, true, remoteMetadata.BlobIdentity, latestSnapshotIdentity), shard.Entries);
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

    private async Task<BlobMetadata> UploadShardAsync(PathSegment prefix, Shard shard, CancellationToken cancellationToken)
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

    private void WithLocalStoreRecovery(Action action)
    {
        try
        {
            action();
        }
        catch (SqliteException ex)
        {
            RecoverLocalStore(ex);
            action();
        }
    }

    private async Task<T> WithLocalStoreRecoveryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SqliteException ex)
        {
            RecoverLocalStore(ex);
            return await action();
        }
    }

    private async Task WithLocalStoreRecoveryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SqliteException ex)
        {
            RecoverLocalStore(ex);
            await action();
        }
    }

    private void RecoverLocalStore(SqliteException ex)
    {
        if (_localStore.HasDirtyMarker())
            throw new InvalidOperationException("Local chunk-index cache is corrupt while dirty rows may exist. Rerun archive or delete the local .arius folder.", ex);

        _localStore.RecreateDatabase(backupExisting: true);
    }

    private       bool IsRepairMarker()     => _repositoryFileSystem.FileExists(RepairInProgressMarkerPath);
    private async Task AddRepairMarker()    => await _repositoryFileSystem.WriteAllBytesAsync(RepairInProgressMarkerPath, [], CancellationToken.None);
    private       void DeleteRepairMarker() => _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);
}
