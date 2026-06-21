using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
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

    // -- Flush ---------------------------------------------------------------

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
    /// Flushes all pending entries in one root subtree, holding the root gate: resolves the authoritative
    /// target shard per pending hash, then builds and uploads the balanced leaf shards for those targets via
    /// <see cref="BuildAndUploadShardsAsync"/>. A target whose range overflowed is split into deeper leaves by
    /// the DB-driven descent; its now-stale parent is deleted afterward via <see cref="DeleteStaleShardsAfterSplitAsync"/>.
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
                .OrderBy(prefix => prefix.ToString(), StringComparer.Ordinal)
                .ToList();

            // Build and upload every target's leaf shards through the shared producer/consumer pipeline.
            var leafPrefixes = await BuildAndUploadShardsAsync(flushTargets, uploadedStates, cancellationToken);

            // A target whose range overflowed was split into deeper leaves, leaving a now-stale parent
            // (and any interrupted-split leftovers) behind; delete them only after the leaves have landed,
            // so a crash mid-split keeps parent-wins lookup correct. A target that fit needs no cleanup.
            foreach (var target in flushTargets)
            {
                // Targets are pairwise non-nested, so each uploaded leaf maps to exactly one target.
                var written = leafPrefixes.Where(prefix => prefix.ToString().StartsWith(target.ToString(), StringComparison.Ordinal)).ToList();
                if (written.Count == 0 || (written.Count == 1 && written[0] == target))
                    continue;

                _logger.LogInformation("Split shard {Prefix} into {LeafCount} leaves", target, written.Count);
                await DeleteStaleShardsAfterSplitAsync(root, target, written, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// After a split's leaf shards have all landed, deletes the now-stale parent and any other blob in
    /// range(prefix) that was not just written — including leftovers of a previously interrupted split.
    /// Uploading the leaves before this delete keeps parent-wins lookup correct if a crash interrupts the
    /// split: the parent stays intact, and since this run's snapshot is not yet published it still contains
    /// everything any published snapshot references. The pending rows stay pending, so a retry re-splits.
    /// </summary>
    private async Task DeleteStaleShardsAfterSplitAsync(PathSegment root, PathSegment prefix, IReadOnlyList<PathSegment> writtenLeaves, CancellationToken cancellationToken)
    {
        // Whether range(prefix) held any remote shard before this run's flush. Under the single-writer
        // assumption the run-scoped listing is that pre-flush state (our own leaf uploads never appear in
        // the immutable snapshot), so an empty range means there is nothing stale to clean.
        var rangeWasEmpty = !((await _shardListing.GetAsync(cancellationToken)).GetValueOrDefault(root.ToString()) ?? FrozenDictionary<string, string?>.Empty)
            .Keys.Any(name => name.StartsWith(prefix.ToString(), StringComparison.Ordinal));
        if (rangeWasEmpty)
            return;

        // Delete the parent and every other blob in range(prefix) that was not just written —
        // including leftovers of a previously interrupted split (their extra entries were never
        // published; the machine that wrote them still has them as pending rows and will re-flush).
        // The destructive scan reads fresh remote state; deletes run concurrently.
        var written = writtenLeaves.Select(leaf => leaf.ToString()).ToHashSet(StringComparer.Ordinal);
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


    // -- SHARD BUILD & UPLOAD -------------------

    /// <summary>
    /// Builds the balanced leaf shards for the given base prefixes and uploads them, recording each
    /// prefix→etag in <paramref name="uploadedStates"/>. <c>BuildShards</c> streams shards lazily as the
    /// single producer (sequential local-store reads) while <see cref="FlushWorkers"/> consumers upload in
    /// parallel; <c>Parallel.ForEachAsync</c> serializes the enumeration and bounds in-flight shards to
    /// ~<see cref="FlushWorkers"/>, so peak memory is independent of repository size. Returns the uploaded
    /// leaf prefixes.
    /// </summary>
    private async Task<IReadOnlyList<PathSegment>> BuildAndUploadShardsAsync(IReadOnlyList<PathSegment> basePrefixes, ConcurrentDictionary<PathSegment, string> uploadedStates, CancellationToken cancellationToken)
    {
        var uploaded = new ConcurrentBag<PathSegment>();
        await Parallel.ForEachAsync(
            basePrefixes.SelectMany(BuildShards),
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (shard, ct) =>
            {
                var result = await UploadShardAsync(shard.Prefix, shard.Shard, ct);
                uploadedStates[shard.Prefix] = result.ETag;
                uploaded.Add(shard.Prefix);
            });

        return uploaded.ToList();
    }

    /// <summary>
    /// Descends the local store's hash space for one prefix: a range that fits the threshold is a single
    /// shard, read from the store; a range that overflows is split 16-way by the next hex character and each
    /// child descended. The database drives the shape — there is no in-memory split, and only one shard is
    /// ever resident regardless of repository size or hash distribution. Recursion terminates by full hash
    /// length (a 64-char prefix is a single content hash); SHA-256 uniformity bottoms out one or two levels
    /// below the 2-char root.
    /// </summary>
    private IEnumerable<(PathSegment Prefix, Shard Shard)> BuildShards(PathSegment prefix)
    {
        var count = _localStore.CountRangeEntries(prefix);
        if (count == 0)
            yield break;

        if (count <= _maxShardEntryCount)
        {
            yield return (prefix, BuildShard(prefix));
            yield break;
        }

        foreach (var child in ChunkIndexRouter.ChildPrefixes(prefix))
            foreach (var shard in BuildShards(child))
                yield return shard;
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
            blobName: BlobPaths.ChunkIndexShardPath(prefix),
            content: new MemoryStream(bytes),
            metadata: new Dictionary<string, string>(),
            tier: BlobTier.Cool,
            contentType: _encryption.IsEncrypted ? ContentTypes.ChunkIndexGcmEncrypted : ContentTypes.ChunkIndexPlaintext,
            overwrite: true,
            cancellationToken: cancellationToken);
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

    // -- Stats ---------------------------------------------------------------

    /// <summary>
    /// Aggregates distinct-chunk count and stored size per storage tier from the local cache only —
    /// no blob reads (the cache is the local mirror of the index, populated by browsing/lookups and
    /// pending archive entries). Deduping is by chunk hash, since tar-bundled content hashes share
    /// one chunk.
    /// </summary>
    public IReadOnlyList<ChunkTierStatistic> GetStatistics()
    {
        ThrowIfRepairIncomplete();
        ThrowIfFlushed();

        return _localStore.GetStatistics();
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

        // Rebuild the entries from a single listing of the chunk blobs. Large chunks resolve inline. A thin
        // chunk's data lives in its parent tar (the thin stub itself is always uploaded Cool), so its tier and
        // chunk size come from the tar blob — which, ordered by hash, may be listed after the thin chunk.
        // Rather than buffer every thin chunk, we write each thin row to the DB during the listing with its
        // parent in chunk_hash and placeholder tier/size, remember only the distinct parent-tar hashes, and
        // enrich them in one bulk pass once the listing (and thus every tar) is known. Peak memory scales with
        // the tar count, not the small-file count, and a single listing keeps the paged remote scan minimal.
        const int writeBatchSize = 1024; // entries per UpsertRemoteBacked call; Chunk yields the partial tail automatically

        var tarMetadata       = new Dictionary<ChunkHash, (BlobTier Tier, long ChunkSize)>();
        var referencedParents = new HashSet<ChunkHash>(); // distinct parent tars referenced by thin chunks; O(#tars), not O(#small-files)

        var listedChunkCount  = 0;
        var rebuiltEntryCount = 0;

        await foreach (var batch in GetRepairEntriesAsync(cancellationToken).Chunk(writeBatchSize).WithCancellation(cancellationToken))
        {
            _localStore.UpsertRemoteBacked(batch);
            rebuiltEntryCount += batch.Length;
        }

        // Every thin row was written with its parent in chunk_hash and placeholder tier/size; fill those in from
        // the now-complete tar metadata. A referenced parent tar absent from the listing means the repository is
        // broken — fail the repair rather than persist a guessed (hydrated) tier.
        var missingParents = referencedParents.Where(parent => !tarMetadata.ContainsKey(parent)).ToList();
        if (missingParents.Count > 0)
            throw new ChunkIndexRepairException(BlobPaths.ChunkPath(missingParents[0]), $"parent tar chunk {missingParents[0]} not found in repository listing");
        _localStore.EnrichThinChunks(tarMetadata);

        // Build and upload a fresh balanced layout from the staged entries through the shared
        // producer/consumer pipeline. This also re-balances an over-split remote layout (the
        // stale-shard pass below deletes everything not in the rebuilt set).
        var uploadedStates     = new ConcurrentDictionary<PathSegment, string>();
        var basePrefixes       = _localStore.GetStoredRootPrefixes().ToList();
        var rebuiltPrefixes    = (await BuildAndUploadShardsAsync(basePrefixes, uploadedStates, cancellationToken)).ToHashSet();
        var uploadedShardCount = rebuiltPrefixes.Count;

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


        // Get the repair entries. Large chunks resolve inline; a thin chunk is yielded with its parent in chunk_hash and placeholder tier/size and enriched after the listing.
        // A tar contributes no entry of its own — it is recovered via its thin chunks.
        async IAsyncEnumerable<ShardEntry> GetRepairEntriesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            // v5 > v7 migration cannot set metadata on chunks in archive tier, so the migration puts a sidecar at chunk-descriptors/{hash}.
            // a chunk whose own metadata lacks arius_type falls back to its sidecar.
            var sidecarMetadata = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            await foreach (var sidecar in _blobs.ListAsync(BlobPaths.V5LegacySideCarPrefix, includeMetadata: true, cancellationToken: ct))
            {
                if (sidecar.Metadata is { Count: > 0 })
                    sidecarMetadata[sidecar.Name.Name.ToString()] = sidecar.Metadata;
            }

            await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: ct))
            {
                ct.ThrowIfCancellationRequested();
                listedChunkCount++;

                // Resolve the correct meteadata: the chunk's own metadata when present, otherwise its sidecar.
                var ownMetadata = item.Metadata ?? throw new ChunkIndexRepairException(item.Name, "metadata was not loaded for repair listing");
                var resolvedMetadata = ownMetadata.ContainsKey(BlobMetadataKeys.AriusType)
                    ? ownMetadata
                    : sidecarMetadata.GetValueOrDefault(item.Name.Name.ToString());
                if (resolvedMetadata is null || !resolvedMetadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType))
                {
                    _logger.LogWarning("Skipping chunk {Chunk} with no arius_type in metadata or sidecar", item.Name);
                    continue;
                }

                switch (ariusType)
                {
                    case BlobMetadataKeys.TypeLarge:
                    {
                        var contentHash  = ContentHash.Parse(item.Name.Name.ToString());
                        var originalSize = ReadRequiredLong(resolvedMetadata, item, BlobMetadataKeys.OriginalSize);
                        var chunkSize    = ReadChunkSize(resolvedMetadata, item);
                        yield return new ShardEntry(contentHash, ChunkHash.Parse(contentHash), originalSize, chunkSize, item.Tier ?? BlobTier.Hot);
                        break;
                    }
                    case BlobMetadataKeys.TypeThin:
                    {
                        var contentHash = ContentHash.Parse(item.Name.Name.ToString());
                        var parentChunkHash = ReadRequiredChunkHash(resolvedMetadata, item, BlobMetadataKeys.ParentChunkHash);
                        referencedParents.Add(parentChunkHash);
                        yield return new ShardEntry(contentHash, parentChunkHash, ReadRequiredLong(resolvedMetadata, item, BlobMetadataKeys.OriginalSize), ChunkSize: 0, StorageTierHint: BlobTier.Cool);
                        break;
                    }
                    case BlobMetadataKeys.TypeTar when ChunkHash.TryParse(item.Name.Name.ToString(), out var tarHash):
                    {
                        tarMetadata[tarHash] = (item.Tier ?? BlobTier.Hot, ReadChunkSize(resolvedMetadata, item));
                        break;
                    }
                }
            }

            static long ReadRequiredLong(IReadOnlyDictionary<string, string> descriptor, BlobListItem item, string key)
                => descriptor.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
                    ? parsed : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");

            static ChunkHash ReadRequiredChunkHash(IReadOnlyDictionary<string, string> descriptor, BlobListItem item, string key)
                => descriptor.TryGetValue(key, out var value) && ChunkHash.TryParse(value, out var parsed)
                    ? parsed : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");

            static long ReadChunkSize(IReadOnlyDictionary<string, string> descriptor, BlobListItem item)
                => descriptor.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
                    ? size : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");
        }
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
    /// No-op. The local SQLite store opens and closes a pooled connection per operation and holds no resources
    /// that outlive a call, so there is nothing to release here; this exists only to satisfy
    /// <see cref="IDisposable"/> on <see cref="IChunkIndexService"/>.
    /// </summary>
    public void Dispose()
    {
    }
}
