using System.Collections.Concurrent;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Repository chunk-index service backed by remote shard blobs and a local SQLite cache.
/// Pending local entries always win over cached remote-backed entries until <see cref="FlushAsync"/> uploads them.
/// </summary>
[SharedWithinAssembly]
internal sealed class ChunkIndexService : IChunkIndexService
{
    internal const           int          MinShardPrefixLength       = 2;
    internal const           int          MaxShardEntryCount         = 10_000;
    internal const           int          FlushWorkers               = 32;
    internal const           int          PrefixLoadWorkers          = 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    private readonly IBlobContainerService                            _blobs;
    private readonly IEncryptionService                               _encryption;
    private readonly ICompressionService                              _compression;
    private readonly RelativeFileSystem                               _repositoryFileSystem;
    private readonly ChunkIndexLocalStore                             _localStore;
    private readonly ConcurrentDictionary<PathSegment, SemaphoreSlim> _rootGates = [];
    private readonly AsyncLazy<string>                                _latestSnapshotName;
    private readonly ILogger<ChunkIndexService>                       _logger;
    private readonly int                                              _maxShardEntryCount;
    private          int                                              _acceptingEntries = 1;

    // -- Construction --------------------------------------------------------

    /// <summary>
    /// Creates a chunk-index service for one repository and its local cache state.
    /// </summary>
    /// <param name="blobs">Blob storage used for remote chunk-index shard reads and writes.</param>
    /// <param name="encryption">Encryption used when serializing and deserializing shard blobs.</param>
    /// <param name="snapshotService">Snapshot service used to identify the latest snapshot for cache validation.</param>
    /// <param name="accountName">Storage account name used to derive the local repository state path.</param>
    /// <param name="containerName">Container name used to derive the local repository state path.</param>
    /// <param name="loggerFactory">Optional logger factory for chunk-index and local-store diagnostics.</param>
    /// <param name="maxShardEntryCount">Shard split threshold; overridable so tests can split with few entries.</param>
    public ChunkIndexService(
        IBlobContainerService blobs,
        IEncryptionService encryption,
        ICompressionService compression,
        ISnapshotService snapshotService,
        string accountName,
        string containerName,
        ILoggerFactory? loggerFactory = null,
        int maxShardEntryCount = MaxShardEntryCount)
    {
        _blobs              = blobs;
        _encryption         = encryption;
        _compression        = compression;
        _maxShardEntryCount = maxShardEntryCount;
        _logger             = loggerFactory?.CreateLogger<ChunkIndexService>() ?? NullLogger<ChunkIndexService>.Instance;

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
    /// Resolves chunk-index entries for the specified content hashes, loading and validating remote-backed prefixes as needed.
    /// </summary>
    /// <param name="contentHashes">Content hashes to resolve.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>A dictionary containing the entries that exist for the requested content hashes.</returns>
    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        var hashes = contentHashes.Distinct().ToArray();
        var result = new Dictionary<ContentHash, ShardEntry>(hashes.Length);
        if (hashes.Length == 0)
            return result;

        var validationWork = new List<(PathSegment Root, List<ContentHash> Hashes)>();
        foreach (var rootGroup in hashes.GroupBy(ChunkIndexRouter.GetRootPrefix))
        {
            var hashesNeedingValidation = new List<ContentHash>();
            foreach (var contentHash in rootGroup)
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

            validationWork.Add((rootGroup.Key, hashesNeedingValidation));
        }

        if (validationWork.Count == 0)
            return result;

        var latestSnapshotName = await _latestSnapshotName;

        // Load the distinct root subtrees concurrently: on a cold cache each root costs a subtree
        // listing plus shard downloads, and even a small batch spans many roots.
        // EnsureCoverageForHashesAsync is safe for concurrent callers (per-root gates).
        await Parallel.ForEachAsync(
            validationWork,
            new ParallelOptions { MaxDegreeOfParallelism = PrefixLoadWorkers, CancellationToken = cancellationToken },
            async (item, ct) => await EnsureCoverageForHashesAsync(item.Root, item.Hashes, latestSnapshotName, ct));

        foreach (var item in validationWork)
        {
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
    /// Resolves the chunk-index entry for a single content hash, loading and validating its remote-backed prefix as needed.
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
        {
            _logger.LogDebug("[chunk-index] Lookup: hash={ContentHash} hit=pending", contentHash.Short8);
            return pendingFlushEntry;
        }

        var latestSnapshot = await _latestSnapshotName;
        await EnsureCoverageForHashesAsync(ChunkIndexRouter.GetRootPrefix(contentHash), [contentHash], latestSnapshot, cancellationToken);
        return _localStore.FindEntry(contentHash);
    }

    /// <summary>
    /// Marks loaded prefixes already validated against the current snapshot as valid for the newly published snapshot.
    /// </summary>
    public async Task PromoteToSnapshotVersionAsync(string newSnapshotVersion)
    {
        ThrowIfRepairIncomplete();

        var oldSnapshotVersion = await _latestSnapshotName;
        if (StringComparer.Ordinal.Equals(oldSnapshotVersion, newSnapshotVersion))
            return;

        _localStore.PromoteToSnapshotVersion(oldSnapshotVersion, newSnapshotVersion);
        _logger.LogDebug("[chunk-index] Promoted cache validation: from={OldSnapshotVersion} to={NewSnapshotVersion}", oldSnapshotVersion, newSnapshotVersion);
    }

    // -- Synchronization -----------------------------------------------------

    /// <summary>
    /// Ensures every hash in <paramref name="hashes"/> (all within the <paramref name="root"/>
    /// subtree) has validated local coverage, taking the root gate.
    /// </summary>
    private async Task EnsureCoverageForHashesAsync(PathSegment root, IReadOnlyList<ContentHash> hashes, string latestSnapshotVersion, CancellationToken cancellationToken)
    {
        var gate = _rootGates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureCoverageCoreAsync(root, hashes, latestSnapshotVersion, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Gate-free core: ensures every hash has a validated coverage claim at
    /// <paramref name="latestSnapshotVersion"/>, loading or revalidating the authoritative shard
    /// per hash from one subtree listing. Returns the shard target per hash (the existing
    /// authoritative shard, or the terminal walk depth when the hash's range is empty).
    /// The caller must hold the root gate.
    /// </summary>
    private async Task<IReadOnlyDictionary<ContentHash, PathSegment>> EnsureCoverageCoreAsync(PathSegment root, IReadOnlyList<ContentHash> hashes, string latestSnapshotVersion, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<ContentHash, PathSegment>(hashes.Count);
        List<ContentHash>? uncovered = null;
        var coveredPrefixes = _localStore.FindCoveredPrefixes(root, hashes, latestSnapshotVersion);
        foreach (var contentHash in hashes)
        {
            if (coveredPrefixes.TryGetValue(contentHash, out var covered))
                targets[contentHash] = covered;
            else
                (uncovered ??= []).Add(contentHash);
        }

        if (uncovered is null)
        {
            // Hot path: all hashes covered by validated claims — zero remote calls.
            _logger.LogDebug("[chunk-index] root {Root}: local cache current for {HashCount} hash(es)", root, hashes.Count);
            return targets;
        }

        var retriedListing = false;
        while (true)
        {
            // One subtree listing decides, per hash, between "existing shard" (parent-wins walk)
            // and "empty range at this depth" — a missing blob alone can mean either.
            var existingRemoteShards = await ListShardSubtreeAsync(root, cancellationToken);
            var existingRemoteShardNames = existingRemoteShards.Keys.ToHashSet(StringComparer.Ordinal);
            _logger.LogDebug("[chunk-index] root {Root}: listed {ShardCount} remote shard(s), uncoveredHashes={UncoveredHashCount}", root, existingRemoteShards.Count, uncovered.Count);

            var emptyPrefixes = new HashSet<PathSegment>();
            var shardsToLoad = new Dictionary<PathSegment, string?>();
            foreach (var contentHash in uncovered)
            {
                var targetShard = ChunkIndexRouter.ResolveTarget(existingRemoteShardNames, contentHash);
                targets[contentHash] = targetShard.Prefix;
                if (targetShard.Exists)
                    shardsToLoad[targetShard.Prefix] = existingRemoteShards[targetShard.Prefix.ToString()];
                else
                    emptyPrefixes.Add(targetShard.Prefix);
            }

            foreach (var prefix in emptyPrefixes)
            {
                _localStore.AddEmptyPrefix(prefix, latestSnapshotVersion);
                _logger.LogDebug("[chunk-index] shard {Prefix}: no remote shard (empty range)", prefix);
            }

            var lostListingRace = false;
            foreach (var (prefix, listedETag) in shardsToLoad)
            {
                if (listedETag is not null && _localStore.IsPrefixAtETag(prefix, listedETag))
                {
                    _localStore.SetPrefixSnapshotVersion(prefix, listedETag, latestSnapshotVersion);
                    _logger.LogDebug("[chunk-index] shard {Prefix}: cache revalidated (etag unchanged)", prefix);
                    continue;
                }

                var blobName = BlobPaths.ChunkIndexShardPath(prefix);
                var remoteShard = await _blobs.TryDownloadAsync(blobName, cancellationToken);
                if (remoteShard is null)
                {
                    if (!retriedListing)
                    {
                        // Deleted between listing and download (a racing split elsewhere):
                        // re-resolve everything from a fresh listing, once.
                        _logger.LogDebug("[chunk-index] shard {Prefix}: listed but not downloadable; retrying subtree listing", prefix);
                        lostListingRace = true;
                        break;
                    }

                    _localStore.AddEmptyPrefix(prefix, latestSnapshotVersion);
                    _logger.LogWarning("[chunk-index] shard {Prefix}: listed but not downloadable after retry; treating range as empty", prefix);
                    continue;
                }

                await using var stream = remoteShard.Stream;

                Shard shard;
                try
                {
                    shard = ShardSerializer.Deserialize(stream, _encryption, _compression);
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
                {
                    throw new ChunkIndexCorruptException(blobName, ex);
                }

                _localStore.UpdatePrefix(prefix, remoteShard.ETag, latestSnapshotVersion, shard.Entries);
                _logger.LogDebug("[chunk-index] shard {Prefix}: downloaded ({EntryCount} entries)", prefix, shard.Count);
            }

            if (!lostListingRace)
                return targets;

            retriedListing = true;
        }
    }

    /// <summary>
    /// Lists all existing shard blobs in the <paramref name="root"/> subtree (raw name-prefix
    /// listing, so <c>aa</c> matches <c>aa</c>, <c>aa0</c>, <c>aa3f</c>, …) as shard name → ETag.
    /// </summary>
    private async Task<Dictionary<string, string?>> ListShardSubtreeAsync(PathSegment root, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal); 
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunkIndexPrefix / root, BlobListPrefixKind.BlobNamePrefix, cancellationToken: cancellationToken))
            names[item.Name.Name.ToString()] = item.ETag;
        return names;
    }

    // -- AddEntry ------------------------------------------------------------

    /// <summary>
    /// Records a newly discovered chunk-index entry as pending local flush state.
    /// </summary>
    /// <param name="entry">The entry to record.</param>
    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        _localStore.UpsertPendingFlush(entry);
    }

    /// <summary>
    /// Records multiple chunk-index entries as pending local flush state in a single transaction.
    /// </summary>
    /// <param name="entries">The entries to record.</param>
    public void AddEntries(IEnumerable<ShardEntry> entries)
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        _localStore.UpsertPendingFlush(entries);
    }

    // -- Flush & Upload ---------------------------------------------------------------

    /// <summary>
    /// Uploads pending local entries into remote shard blobs and marks flushed prefixes as synchronized remote-backed cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the flush.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (Interlocked.Exchange(ref _acceptingEntries, 0) == 0)
            throw new InvalidOperationException("Chunk-index service cannot be used after flush has started.");

        var rootsWithPendingFlushes = _localStore.GetRootsWithPendingFlushes();
        if (rootsWithPendingFlushes.Count == 0)
        {
            _logger.LogDebug("[chunk-index] No pending shard flushes");
            return; // no shards need to be written to blob
        }

        _logger.LogInformation("[chunk-index] Flushing {RootCount} shard subtrees", rootsWithPendingFlushes.Count);

        var latestSnapshotVersion = await _latestSnapshotName;
        var uploadedStates = new ConcurrentDictionary<PathSegment, string>();

        await Parallel.ForEachAsync(
            rootsWithPendingFlushes,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (root, ct) => await FlushRootAsync(root, latestSnapshotVersion, uploadedStates, ct));

        // Only after EVERY subtree uploaded successfully: pending rows become clean, and the
        // uploaded shard set becomes validated coverage (leaf claims replace any split parent's
        // claim via coverage-overlap deletion).
        _localStore.MarkPendingFlushesSynchronized(uploadedStates.Select(x => (x.Key, x.Value)), latestSnapshotVersion);

        _logger.LogInformation("[chunk-index] Flushed {UploadedCount} shards across {RootCount} subtrees", uploadedStates.Count, rootsWithPendingFlushes.Count);
    }

    /// <summary>
    /// Flushes all pending entries in one root subtree, holding the root gate: resolves the
    /// authoritative target shard per pending hash, merges, and uploads — splitting any shard
    /// that exceeds the entry-count threshold.
    /// </summary>
    private async Task FlushRootAsync(PathSegment root, string latestSnapshotVersion, ConcurrentDictionary<PathSegment, string> uploadedStates, CancellationToken cancellationToken)
    {
        var gate = _rootGates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Resolve targets and ensure the authoritative shards are loaded locally — the local
            // cache may be out of date from a previous run on another machine, and an interrupted
            // split resolves to the still-existing parent here (reloading its full range before
            // the re-split rewrites the children).
            var pendingHashes = _localStore.GetPendingFlushHashes(root);
            _logger.LogDebug("[chunk-index] root {Root}: flushing pendingEntries={PendingEntryCount}", root, pendingHashes.Count);
            var targets = await EnsureCoverageCoreAsync(root, pendingHashes, latestSnapshotVersion, cancellationToken);

            // An interrupted split can yield mixed-depth targets, e.g. a parent plus an
            // already-claimed child. A target nested inside another target is fully covered by
            // the ancestor's range, so only the shallowest targets are flushed.
            var distinctTargets = targets.Values.Distinct().ToList();
            var flushTargets = distinctTargets
                .Where(prefix => !distinctTargets.Any(other => other != prefix && prefix.ToString().StartsWith(other.ToString(), StringComparison.Ordinal)))
                .OrderBy(prefix => prefix.ToString(), StringComparer.Ordinal);

            foreach (var prefix in flushTargets)
            {
                var shard = BuildShard(prefix);
                if (shard.Count == 0)
                    continue;

                if (shard.Count <= _maxShardEntryCount)
                {
                    var result = await UploadShardAsync(prefix, shard, cancellationToken);
                    uploadedStates[prefix] = result.ETag;
                    _logger.LogDebug("[chunk-index] Uploaded shard {Prefix} ({EntryCount} entries)", prefix, shard.Count);
                    continue;
                }

                await SplitShardAsync(root, prefix, shard, uploadedStates, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Splits an over-threshold shard: uploads all non-empty leaf shards FIRST, and only then
    /// deletes the parent and any other stale shard in its range. A crash mid-split leaves the
    /// parent intact; since the snapshot for this run is not yet published, the parent still
    /// contains everything any published snapshot references, and parent-wins lookup stays
    /// correct. The pending rows stay pending, so a retry re-resolves the parent and re-splits.
    /// </summary>
    private async Task SplitShardAsync(PathSegment root, PathSegment prefix, Shard shard, ConcurrentDictionary<PathSegment, string> uploadedStates, CancellationToken cancellationToken)
    {
        var leaves = ChunkIndexRouter.PartitionIntoLeaves(prefix, shard.Entries.ToList(), _maxShardEntryCount);
        foreach (var (leafPrefix, leafEntries) in leaves)
        {
            var leafShard = new Shard();
            leafShard.AddOrUpdateRange(leafEntries);
            var result = await UploadShardAsync(leafPrefix, leafShard, cancellationToken);
            uploadedStates[leafPrefix] = result.ETag;
        }

        _logger.LogInformation("[chunk-index] Split shard {Prefix} ({EntryCount} entries) into {LeafCount} leaves", prefix, shard.Count, leaves.Count);

        // Delete the parent and every other blob in range(prefix) that was not just written —
        // including leftovers of a previously interrupted split (their extra entries were never
        // published; the machine that wrote them still has them as pending rows and will re-flush).
        var written = leaves.Select(leaf => leaf.Prefix.ToString()).ToHashSet(StringComparer.Ordinal);
        var listing = await ListShardSubtreeAsync(root, cancellationToken);
        foreach (var name in listing.Keys.Where(name => name.StartsWith(prefix.ToString(), StringComparison.Ordinal) && !written.Contains(name)))
        {
            await _blobs.DeleteAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse(name)), cancellationToken);
            _logger.LogInformation("Deleted stale shard {Name} after split of {Prefix}", name, prefix);
        }
    }

    /// <summary>
    /// Builds the current shard payload for one prefix range from local store state.
    /// </summary>
    /// <param name="prefix">The shard prefix range to materialize.</param>
    /// <returns>A shard containing all currently stored entries within the prefix range.</returns>
    private Shard BuildShard(PathSegment prefix)
    {
        var shard = new Shard();
        _localStore.ReadRangeEntries(prefix, shard.AddOrUpdate);
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
        var bytes = await ShardSerializer.SerializeAsync(shard, _encryption, _compression, cancellationToken);
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
        _logger.LogInformation("[chunk-index] Invalidating remote-backed local cache");
        _localStore.ClearRemoteBackedCache();
    }

    // -- Repair --------------------------------------------------------------

    /// <summary>
    /// Rebuilds chunk-index shards from authoritative chunk blobs and deletes stale shard blobs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the repair.</param>
    /// <returns>A summary of the repair work that was performed.</returns>
    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFlushed();
        AddRepairMarker();
        _localStore.RecreateDatabase(backupExisting: true);
        _logger.LogInformation("[chunk-index] Repair marker written; rebuilding local cache from chunk blobs");

        // Pass 1: collect tar blob metadata. A thin chunk's data lives in its parent tar (the thin
        // stub itself is always uploaded Cool), so its tier hint and chunk size must come from the
        // tar blob. Keeping this as a separate listing avoids buffering unresolved thin chunks or
        // doing per-thin remote lookups; memory stays bounded by the tar count, not the file count.
        var tarMetadata = new Dictionary<ChunkHash, (BlobTier Tier, long ChunkSize)>();
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Metadata is { } metadata
                && metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType)
                && ariusType == BlobMetadataKeys.TypeTar
                && ChunkHash.TryParse(item.Name.Name.ToString(), out var tarHash))
            {
                tarMetadata[tarHash] = (item.Tier ?? BlobTier.Hot, ReadChunkSize(item));
            }
        }

        _logger.LogInformation("[chunk-index] Repair pass 1 complete: tarChunks={TarChunkCount}", tarMetadata.Count);

        // Pass 2: rebuild the entries once all parent tar metadata is known.
        var listedChunkCount = 0;
        var rebuiltEntryCount = 0;

        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            listedChunkCount++;

            var entry = CreateRepairEntry(item, tarMetadata);
            if (entry is null)
                continue;

            _localStore.UpsertRemoteBacked(entry);
            rebuiltEntryCount++;
        }

        _logger.LogInformation("[chunk-index] Repair pass 2 complete: listedChunks={ListedChunkCount} rebuiltEntries={RebuiltEntryCount}", listedChunkCount, rebuiltEntryCount);

        // Compute a fresh balanced layout from the staged entries: recursively split any range
        // whose entry count exceeds the threshold. This also re-balances an over-split remote
        // layout (the stale-shard pass below deletes everything not in the rebuilt set).
        var rebuiltPrefixes = new HashSet<PathSegment>();
        foreach (var root in _localStore.GetStoredRootPrefixes())
            CollectLeaves(root);

        _logger.LogInformation("[chunk-index] Repair layout planned: rebuiltShards={RebuiltShardCount}", rebuiltPrefixes.Count);

        void CollectLeaves(PathSegment prefix)
        {
            var count = _localStore.CountRangeEntries(prefix);
            if (count == 0)
                return;

            if (count <= _maxShardEntryCount)
            {
                rebuiltPrefixes.Add(prefix);
                return;
            }

            foreach (var child in ChunkIndexRouter.GetChildPrefixes(prefix))
                CollectLeaves(child);
        }

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

        _logger.LogInformation("[chunk-index] Repair uploaded {UploadedShardCount} rebuilt shard(s)", uploadedShardCount);

        // Delete stale shards
        var deletedStaleShardCount = 0;
        await Parallel.ForEachAsync(
            _blobs.ListAsync(BlobPaths.ChunkIndexPrefix, includeMetadata: false, cancellationToken: cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                if (rebuiltPrefixes.Contains(item.Name.Name))
                    return;

                await _blobs.DeleteAsync(item.Name, ct);

                Interlocked.Increment(ref deletedStaleShardCount);
            });

        _logger.LogInformation("[chunk-index] Repair deleted {DeletedStaleShardCount} stale shard(s)", deletedStaleShardCount);

        DeleteRepairMarker();
        _logger.LogDebug("[chunk-index] Repair marker deleted");

        return new ChunkIndexRepairResult(listedChunkCount, rebuiltEntryCount, rebuiltPrefixes.Count, uploadedShardCount, deletedStaleShardCount);
    }

    /// <summary>
    /// Converts a chunk blob listing item into a rebuildable shard entry when that blob contributes to the chunk index.
    /// </summary>
    /// <param name="item">The listed chunk blob.</param>
    /// <param name="tarMetadata">Parent tar chunk tier and size metadata keyed by tar chunk hash.</param>
    /// <returns>The rebuilt shard entry, or <see langword="null"/> when the blob should not appear in the chunk index.</returns>
    private static ShardEntry? CreateRepairEntry(BlobListItem item, IReadOnlyDictionary<ChunkHash, (BlobTier Tier, long ChunkSize)> tarMetadata)
    {
        var metadata = item.Metadata ?? throw new ChunkIndexRepairException(item.Name, "metadata was not loaded for repair listing");
        if (!metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType))
            return null;

        return ariusType switch
        {
            BlobMetadataKeys.TypeLarge => CreateLargeRepairEntry(item),
            BlobMetadataKeys.TypeThin  => CreateThinRepairEntry(item, tarMetadata),
            BlobMetadataKeys.TypeTar   => null, // TAR entries will be recovered by the thin chunks
            _                          => null,
        };

        static ShardEntry CreateLargeRepairEntry(BlobListItem item)
        {
            var contentHash    = ContentHash.Parse(item.Name.Name.ToString());
            var originalSize   = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
            var chunkSize      = ReadChunkSize(item);
            return new ShardEntry(contentHash, ChunkHash.Parse(contentHash), originalSize, chunkSize, item.Tier ?? BlobTier.Hot);
        }

        static ShardEntry CreateThinRepairEntry(BlobListItem item, IReadOnlyDictionary<ChunkHash, (BlobTier Tier, long ChunkSize)> tarMetadata)
        {
            var contentHash = ContentHash.Parse(item.Name.Name.ToString());
            if (!item.Metadata!.TryGetValue(BlobMetadataKeys.ParentChunkHash, out var parentChunkHashValue) || !ChunkHash.TryParse(parentChunkHashValue, out var parentChunkHash))
                throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ParentChunkHash} metadata");

            // The thin stub itself is always uploaded Cool; its data tier is the parent tar's tier.
            // A parent tar absent from the listing means the repository is broken — fail the repair
            // rather than persisting a guessed (hydrated) tier.
            if (!tarMetadata.TryGetValue(parentChunkHash, out var parentTar))
                throw new ChunkIndexRepairException(item.Name, $"parent tar chunk {parentChunkHash} not found in repository listing");

            var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
            return new ShardEntry(contentHash, parentChunkHash, originalSize, parentTar.ChunkSize, parentTar.Tier);
        }

        static long ReadRequiredLongMetadata(BlobListItem item, string key)
            => item.Metadata is not null && item.Metadata.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
                ? parsed
                : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");
    }

    private static long ReadChunkSize(BlobListItem item)
        => item.Metadata is not null && item.Metadata.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
            ? size
            : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");

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
