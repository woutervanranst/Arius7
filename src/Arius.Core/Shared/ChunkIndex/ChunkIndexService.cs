using System.Collections.Concurrent;
using System.Collections.Frozen;
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
    internal const           int          MaxShardEntryCount         = 1024;
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

    // Run-scoped listing of the whole chunk-index/ subtree (shard name -> ETag), grouped by 2-hex root. Under
    // the single-writer assumption the remote layout is stable for a run, so one cached listing serves every
    // root/leaf; per-root re-listing on each uncovered lookup is pure waste. Unlike _latestSnapshotName (a
    // fixed-for-the-run AsyncLazy), the layout can change under us, so this is a ResettableAsyncLazy: it is
    // reset by InvalidateCaches (epoch mismatch), RepairAsync, and once on a 404-race, and it replaces a
    // faulted fetch so a transient list error doesn't poison the run.
    private readonly ResettableAsyncLazy<FrozenDictionary<string, FrozenDictionary<string, string?>>> _shardListing;

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
        _repositoryFileSystem = new(repositoryRoot);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _localStore = new(cacheRoot, loggerFactory?.CreateLogger<ChunkIndexLocalStore>());
        _latestSnapshotName = new(async () =>
        {
            var snapshots = await snapshotService.ListBlobNamesAsync();
            return snapshots.Count == 0
                ? "<none>"
                : snapshots[^1].Name.ToString();
        });
        _shardListing = new(ListAllShardsAsync);
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
                    // Entry is local-only / dirty
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
            async (item, ct) =>
            {
                await EnsureCoverageForHashesAsync(item.Root, item.Hashes, latestSnapshotName, ct);
            });

        // Construct the result from the validated shards
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
            return pendingFlushEntry;

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
    }

    // -- Synchronization -----------------------------------------------------

    /// <summary>
    /// Ensures every hash in <paramref name="hashes"/> (all within the <paramref name="root"/>
    /// subtree) has validated local coverage, taking the root gate.
    /// </summary>
    private async Task EnsureCoverageForHashesAsync(PathSegment root, IReadOnlyList<ContentHash> hashes, string latestSnapshotVersion, CancellationToken cancellationToken)
    {
        var gate = _rootGates.GetOrAdd(root, static _ => new(1, 1));
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
        var coveredPrefixes = _localStore.FindCoveredPrefixes(hashes, latestSnapshotVersion);
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
            _logger.LogDebug("[chunk-index] root {Root}: local cache current", root);
            return targets;
        }

        var retriedListing = false;
        while (true)
        {
            // The run-scoped listing decides, per hash, between "existing shard" (parent-wins walk)
            // and "empty range at this depth" — a missing blob alone can mean either.
            var listing = await _shardListing.GetAsync(cancellationToken);
            var existingRemoteShards = listing.GetValueOrDefault(root.ToString()) ?? FrozenDictionary<string, string?>.Empty;
            var existingRemoteShardNames = existingRemoteShards.Keys.ToHashSet(StringComparer.Ordinal);
            // Precompute the descendant-prefix set once for the whole root so ResolveTarget is O(depth) per hash.
            var descendantPrefixes = ChunkIndexRouter.BuildDescendantPrefixes(existingRemoteShardNames);

            var emptyPrefixes = new HashSet<PathSegment>();
            var shardsToLoad = new Dictionary<PathSegment, string?>();
            foreach (var contentHash in uncovered)
            {
                var targetShard = ChunkIndexRouter.ResolveTarget(existingRemoteShardNames, descendantPrefixes, contentHash);
                targets[contentHash] = targetShard.Prefix;
                if (targetShard.Exists)
                    shardsToLoad[targetShard.Prefix] = existingRemoteShards[targetShard.Prefix.ToString()];
                else
                    emptyPrefixes.Add(targetShard.Prefix);
            }

            // Download + deserialize the distinct shards concurrently (bounded). Collect results; do not
            // touch the local store mid-fan-out — all writes are applied once, in a single transaction.
            var downloaded  = new ConcurrentBag<(PathSegment Prefix, string Etag, IReadOnlyList<ShardEntry> Entries)>();
            var revalidated = new ConcurrentBag<(PathSegment Prefix, string Etag)>();
            var raced       = new ConcurrentBag<PathSegment>();
            await Parallel.ForEachAsync(
                shardsToLoad,
                new ParallelOptions { MaxDegreeOfParallelism = PrefixLoadWorkers, CancellationToken = cancellationToken },
                async (shard, ct) =>
                {
                    await LoadShardAsync(shard.Key, shard.Value, downloaded, revalidated, raced, ct);
                });

            // A shard listed at snapshot time but gone at download time means a racing split deleted it.
            // Reset the shared listing and re-resolve from a fresh one; the per-call latch bounds this to one
            // retry (concurrent racers may each reset, costing a few redundant re-lists in that rare case).
            if (!raced.IsEmpty && !retriedListing)
            {
                _shardListing.Reset();
                retriedListing = true;
                continue;
            }

            // After the single retry a still-missing shard is treated as an empty range.
            foreach (var prefix in raced)
                _logger.LogWarning("[chunk-index] shard {Prefix}: listed but not downloadable after retry; treating range as empty", prefix);

            _localStore.IngestCoverage(
                latestSnapshotVersion,
                downloaded,
                revalidated,
                emptyPrefixes.Concat(raced).ToHashSet());

            return targets;
        }
    }

    /// <summary>
    /// Loads one shard: a cached etag-match revalidates without download; otherwise downloads and
    /// deserializes. A listed-but-missing shard (racing split) is recorded as raced for the caller's
    /// single re-list retry. Concurrency is bounded by the caller's <c>Parallel.ForEachAsync</c>.
    /// </summary>
    private async Task LoadShardAsync(
        PathSegment prefix,
        string? listedETag,
        ConcurrentBag<(PathSegment Prefix, string Etag, IReadOnlyList<ShardEntry> Entries)> downloaded,
        ConcurrentBag<(PathSegment Prefix, string Etag)> revalidated,
        ConcurrentBag<PathSegment> raced,
        CancellationToken cancellationToken)
    {
        if (listedETag is not null && _localStore.IsPrefixAtETag(prefix, listedETag))
        {
            revalidated.Add((prefix, listedETag));
            _logger.LogInformation("[chunk-index] shard {Prefix}: cache revalidated (etag unchanged)", prefix);
            return;
        }

        var blobName = BlobPaths.ChunkIndexShardPath(prefix);
        var remoteShard = await _blobs.TryDownloadAsync(blobName, cancellationToken);
        if (remoteShard is null)
        {
            raced.Add(prefix);
            return;
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

        downloaded.Add((prefix, remoteShard.ETag, shard.Entries.ToList()));
        _logger.LogInformation("[chunk-index] shard {Prefix}: downloaded ({EntryCount} entries)", prefix, shard.Count);
    }

    // -- Shard listing cache -------------------------------------------------

    /// <summary>
    /// Lists the entire <c>chunk-index/</c> subtree once (Azure auto-pages at 5000 blobs/page) and groups
    /// the shard names + ETags by their 2-hex root for O(1) per-root resolution.
    /// </summary>
    private async Task<FrozenDictionary<string, FrozenDictionary<string, string?>>> ListAllShardsAsync(CancellationToken cancellationToken)
    {
        var byRoot = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunkIndexPrefix, BlobListPrefixKind.DirectoryPrefix, cancellationToken: cancellationToken))
        {
            var name = item.Name.Name.ToString();
            if (name.Length < MinShardPrefixLength)
                continue;

            var root = name[..MinShardPrefixLength];
            if (!byRoot.TryGetValue(root, out var shards))
                byRoot[root] = shards = new(StringComparer.Ordinal);
            shards[name] = item.ETag;
        }

        return byRoot.ToFrozenDictionary(
            entry => entry.Key,
            entry => entry.Value.ToFrozenDictionary(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Lists all existing shard blobs in the <paramref name="root"/> subtree (raw name-prefix listing, so
    /// <c>aa</c> matches <c>aa</c>, <c>aa0</c>, <c>aa3f</c>, …) as shard name. Used only by the destructive
    /// post-split delete scan, which always reads fresh remote state rather than the run-scoped cache.
    /// </summary>
    private async Task<HashSet<string>> ListShardSubtreeAsync(PathSegment root, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunkIndexPrefix / root, BlobListPrefixKind.BlobNamePrefix, cancellationToken: cancellationToken))
            names.Add(item.Name.Name.ToString());
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
            _logger.LogDebug("No pending shard flushes");
            return; // no shards need to be written to blob
        }

        _logger.LogInformation("Flushing {RootCount} shard subtrees", rootsWithPendingFlushes.Count);

        var latestSnapshotVersion = await _latestSnapshotName;
        var uploadedStates = new ConcurrentDictionary<PathSegment, string>();

        await Parallel.ForEachAsync(
            rootsWithPendingFlushes,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (root, ct) =>
            {
                await FlushRootAsync(root, latestSnapshotVersion, uploadedStates, ct);
            });

        // Only after EVERY subtree uploaded successfully: pending rows become clean, and the
        // uploaded shard set becomes validated coverage (leaf claims replace any split parent's
        // claim via coverage-overlap deletion).
        _localStore.MarkPendingFlushesSynchronized(uploadedStates.Select(x => (x.Key, x.Value)), latestSnapshotVersion);

        _logger.LogInformation("Flushed {UploadedCount} shards across {RootCount} subtrees", uploadedStates.Count, rootsWithPendingFlushes.Count);
    }

    /// <summary>
    /// Flushes all pending entries in one root subtree, holding the root gate: resolves the
    /// authoritative target shard per pending hash, merges, and uploads — splitting any shard
    /// that exceeds the entry-count threshold.
    /// </summary>
    private async Task FlushRootAsync(PathSegment root, string latestSnapshotVersion, ConcurrentDictionary<PathSegment, string> uploadedStates, CancellationToken cancellationToken)
    {
        var gate = _rootGates.GetOrAdd(root, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Resolve targets and ensure the authoritative shards are loaded locally — the local
            // cache may be out of date from a previous run on another machine, and an interrupted
            // split resolves to the still-existing parent here (reloading its full range before
            // the re-split rewrites the children).
            var pendingHashes = _localStore.GetPendingFlushHashes(root);
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
                    _logger.LogDebug("Uploaded shard {Prefix} ({EntryCount} entries)", prefix, shard.Count);
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
        // Whether range(prefix) held any remote shard before this run's flush. Under the single-writer
        // assumption the run-scoped listing is that pre-flush state (our own leaf uploads below never
        // appear in the immutable snapshot), so an empty range means there is nothing stale to clean.
        var rangeWasEmpty = !((await _shardListing.GetAsync(cancellationToken)).GetValueOrDefault(root.ToString()) ?? FrozenDictionary<string, string?>.Empty)
            .Keys.Any(name => name.StartsWith(prefix.ToString(), StringComparison.Ordinal));

        var leaves = ChunkIndexRouter.PartitionIntoLeaves(prefix, shard.Entries.ToList(), _maxShardEntryCount);

        // Upload the (independent) leaf shards concurrently. ALL must land before any delete: a crash
        // mid-split must leave the parent intact so parent-wins lookup stays correct.
        await Parallel.ForEachAsync(
            leaves,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (leaf, ct) =>
            {
                var leafShard = new Shard();
                leafShard.AddOrUpdateRange(leaf.Entries);
                var result = await UploadShardAsync(leaf.Prefix, leafShard, ct);
                uploadedStates[leaf.Prefix] = result.ETag;
            });

        _logger.LogInformation("Split shard {Prefix} ({EntryCount} entries) into {LeafCount} leaves", prefix, shard.Count, leaves.Count);

        // A brand-new (empty) range had no parent or interrupted-split leftovers, so the post-split
        // subtree listing and deletes are pure waste — skip them.
        if (rangeWasEmpty)
            return;

        // Delete the parent and every other blob in range(prefix) that was not just written —
        // including leftovers of a previously interrupted split (their extra entries were never
        // published; the machine that wrote them still has them as pending rows and will re-flush).
        // The destructive scan reads fresh remote state; deletes run concurrently.
        var written = leaves.Select(leaf => leaf.Prefix.ToString()).ToHashSet(StringComparer.Ordinal);
        var listing = await ListShardSubtreeAsync(root, cancellationToken);
        var stale = listing.Where(name => name.StartsWith(prefix.ToString(), StringComparison.Ordinal) && !written.Contains(name)).ToList();
        await Parallel.ForEachAsync(
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (name, ct) =>
            {
                await _blobs.DeleteAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse(name)), ct);
                _logger.LogInformation("Deleted stale shard {Name} after split of {Prefix}", name, prefix);
            });
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
        _localStore.ClearRemoteBackedCache();
        // Drop the run-scoped listing too: an epoch mismatch means another machine published shards we
        // have not seen, so the flush that follows must re-list fresh rather than reuse a stale layout.
        _shardListing.Reset();
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

        // Compute a fresh balanced layout from the staged entries: recursively split any range
        // whose entry count exceeds the threshold. This also re-balances an over-split remote
        // layout (the stale-shard pass below deletes everything not in the rebuilt set).
        var rebuiltPrefixes = new HashSet<PathSegment>();
        foreach (var root in _localStore.GetStoredRootPrefixes())
            CollectLeaves(root);

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

        DeleteRepairMarker();
        _shardListing.Reset(); // repair rewrote the whole layout — drop any cached listing

        return new(listedChunkCount, rebuiltEntryCount, rebuiltPrefixes.Count, uploadedShardCount, deletedStaleShardCount);
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
            return new(contentHash, ChunkHash.Parse(contentHash), originalSize, chunkSize, item.Tier ?? BlobTier.Hot);
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
            return new(contentHash, parentChunkHash, originalSize, parentTar.ChunkSize, parentTar.Tier);
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
