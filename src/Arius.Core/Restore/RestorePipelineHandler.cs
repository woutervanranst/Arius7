using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Arius.Core.Streaming;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Formats.Tar;
using System.IO.Compression;

namespace Arius.Core.Restore;

/// <summary>
/// Implements the full restore pipeline as a Mediator command handler.
///
/// Flow:
/// 1. Resolve snapshot (tasks 10.1, 10.2)
/// 2. Traverse tree to collect file entries matching target path
/// 3. Conflict check: skip identical local files; honour --overwrite (task 10.3)
/// 4. Chunk resolution via chunk index (task 10.4)
/// 5. Rehydration status check (task 10.5)
/// 6. Cost estimation — delegated to CLI via events (task 10.6)
/// 7. Phase 1: download available chunks (task 10.7)
/// 8. Phase 2: kick off rehydration for archive-tier chunks (task 10.8)
/// 9. Pointer file creation (task 10.11)
/// 10. Progress event emission (task 10.12)
/// </summary>
public sealed class RestorePipelineHandler
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    private readonly IBlobStorageService              _blobs;
    private readonly IEncryptionService               _encryption;
    private readonly ChunkIndexService                _index;
    private readonly IMediator                        _mediator;
    private readonly ILogger<RestorePipelineHandler>  _logger;
    private readonly string                           _accountName;
    private readonly string                           _containerName;

    public RestorePipelineHandler(
        IBlobStorageService            blobs,
        IEncryptionService             encryption,
        ChunkIndexService              index,
        IMediator                      mediator,
        ILogger<RestorePipelineHandler> logger,
        string                         accountName,
        string                         containerName)
    {
        _blobs         = blobs;
        _encryption    = encryption;
        _index         = index;
        _mediator      = mediator;
        _logger        = logger;
        _accountName   = accountName;
        _containerName = containerName;
    }

    /// <summary>
    /// Executes the end-to-end restore pipeline for the provided RestoreCommand.
    /// </summary>
    /// <remarks>
    /// Orchestrates snapshot resolution, tree traversal, local conflict handling, chunk index lookup and grouping, downloading or extracting chunk data, optional archive rehydration requests and cleanup, pointer-file creation, and publishes progress/reconciliation events.
    /// </remarks>
    /// <param name="command">The restore command containing options such as RootDirectory, Version, TargetPath, Overwrite, and confirmation callbacks.</param>
    /// <param name="cancellationToken">Token to observe while performing asynchronous operations.</param>
    /// <returns>
    /// A RestoreResult indicating whether the operation succeeded, how many files were restored, how many were skipped, the number of chunks pending rehydration, and an error message when unsuccessful.
    /// <summary>
    /// Orchestrates the complete restore pipeline for the provided <see cref="RestoreCommand"/>, including snapshot resolution, file selection and conflict handling, chunk lookup and grouping, downloading and extracting chunks, optional archive rehydration requests, and progress/event publishing.
    /// </summary>
    /// <param name="command">The command containing <see cref="RestoreOptions"/> that control root directory, target path, overwrite behavior, and optional confirmation callbacks for rehydration and cleanup.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>
    /// A <see cref="RestoreResult"/> describing whether the restore succeeded, how many files were restored or skipped, how many chunks remain pending rehydration, and an error message when the operation fails.
    /// </returns>
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // ── Operation start marker (task 4.7) ────────────────────────────────
        _logger.LogInformation("[restore] Start: target={RootDir} account={Account} container={Container} version={Version} overwrite={Overwrite}", opts.RootDirectory, _accountName, _containerName, opts.Version ?? "latest", opts.Overwrite);

        // ── Ensure container exists ───────────────────────────────────────────
        await _blobs.CreateContainerIfNotExistsAsync(cancellationToken);

        try
        {
            // ── Step 1: Resolve snapshot ──────────────────────────────────────

            var snapshotSvc = new SnapshotService(_blobs, _encryption);
            var snapshot    = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);

            if (snapshot is null)
            {
                return new RestoreResult
                {
                    Success             = false,
                    FilesRestored       = 0,
                    FilesSkipped        = 0,
                    ChunksPendingRehydration = 0,
                    ErrorMessage        = opts.Version is null
                        ? "No snapshots found in this repository."
                        : $"Snapshot '{opts.Version}' not found."
                };
            }

            _logger.LogInformation("[snapshot] Resolved: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash[..8]);

            // ── Step 2: Tree traversal ────────────────────────────────────────

            var treeCache = new TreeBuilder(_blobs, _encryption, _accountName, _containerName);
            var files     = await CollectFilesAsync(snapshot.RootHash, opts.TargetPath, treeCache, cancellationToken);

            _logger.LogInformation("[tree] Traversal complete: {Count} file(s) collected", files.Count);

            // Publish snapshot resolved after tree traversal so we include file count (from tree)
            await _mediator.Publish(new SnapshotResolvedEvent(snapshot.Timestamp, snapshot.RootHash, files.Count), cancellationToken);
            await _mediator.Publish(new TreeTraversalCompleteEvent(files.Count, TotalOriginalSize: 0), cancellationToken);
            await _mediator.Publish(new RestoreStartedEvent(files.Count), cancellationToken);

            // ── Step 3: Conflict check ────────────────────────────────────────

            var toRestore = new List<FileToRestore>();
            int skipped   = 0;

            foreach (var file in files)
            {
                var localPath = Path.Combine(opts.RootDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(localPath))
                {
                    if (!opts.Overwrite)
                    {
                        // Hash local file to check if already correct
                        await using var fs = File.OpenRead(localPath);
                        var hashBytes = await _encryption.ComputeHashAsync(fs, cancellationToken);
                        var localHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                        if (localHash == file.ContentHash)
                        {
                            _logger.LogInformation("[disposition] {Path} -> skip (identical)", file.RelativePath);
                            skipped++;
                            await _mediator.Publish(new FileSkippedEvent(file.RelativePath, fs.Length), cancellationToken);
                            await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.SkipIdentical, fs.Length), cancellationToken);
                            continue;
                        }

                        // File exists with different hash, no --overwrite → keep local
                        _logger.LogInformation("[disposition] {Path} -> keep (local differs, no --overwrite)", file.RelativePath);
                        skipped++;
                        await _mediator.Publish(new FileSkippedEvent(file.RelativePath, fs.Length), cancellationToken);
                        await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.KeepLocalDiffers, fs.Length), cancellationToken);
                        continue;
                    }
                    else
                    {
                        _logger.LogInformation("[disposition] {Path} -> overwrite", file.RelativePath);
                        var fi = new FileInfo(localPath);
                        await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.Overwrite, fi.Length), cancellationToken);
                    }
                }
                else
                {
                    _logger.LogInformation("[disposition] {Path} -> new", file.RelativePath);
                    await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.New, 0), cancellationToken);
                }

                toRestore.Add(file);
            }

            int filesRestored    = 0;
            long filesRestoredLong = 0;
            int totalPending     = 0;

            if (toRestore.Count > 0)
            {

            // ── Step 4: Chunk resolution ──────────────────────────────────────

            var contentHashes = toRestore.Select(f => f.ContentHash).Distinct().ToList();
            var indexEntries  = await _index.LookupAsync(contentHashes, cancellationToken);

            // Group files by chunk hash
            var filesByChunkHash = new Dictionary<string, List<FileToRestore>>(StringComparer.Ordinal);
            var unresolved = new List<FileToRestore>();

            foreach (var file in toRestore)
            {
                if (!indexEntries.TryGetValue(file.ContentHash, out var entry))
                {
                    _logger.LogWarning("Content hash not found in index, skipping: {Hash} ({Path})", file.ContentHash, file.RelativePath);
                    unresolved.Add(file);
                    continue;
                }

                if (!filesByChunkHash.TryGetValue(entry.ChunkHash, out var list))
                    filesByChunkHash[entry.ChunkHash] = list = new List<FileToRestore>();
                list.Add(file);
            }

            int largeChunks = filesByChunkHash.Keys.Count(k => indexEntries.TryGetValue(filesByChunkHash[k][0].ContentHash, out var ie) && ie.ContentHash == ie.ChunkHash);
            int tarChunks   = filesByChunkHash.Count - largeChunks;

            // Sum original and compressed sizes from index entries for the aggregate counters.
            // For large files, sizes come from the single index entry.
            // For tar bundles, ShardEntry.CompressedSize is a proportional per-file share;
            // sum across all files to reconstruct the total tar.gz blob size.
            long totalOriginalBytes   = 0;
            long totalCompressedBytes = 0;
            foreach (var chunkHash in filesByChunkHash.Keys)
            {
                var firstFile = filesByChunkHash[chunkHash][0];
                if (indexEntries.TryGetValue(firstFile.ContentHash, out var ie2))
                {
                    bool isLargeChunk = ie2.ContentHash == ie2.ChunkHash;
                    if (isLargeChunk)
                    {
                        totalOriginalBytes   += ie2.OriginalSize;
                        totalCompressedBytes += ie2.CompressedSize;
                    }
                    else
                    {
                        // Tar bundle: sum across all files that map to this chunk
                        foreach (var file in filesByChunkHash[chunkHash])
                        {
                            if (indexEntries.TryGetValue(file.ContentHash, out var fileEntry))
                            {
                                totalOriginalBytes   += fileEntry.OriginalSize;
                                totalCompressedBytes += fileEntry.CompressedSize;
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("[chunk] Resolution: {Groups} chunk group(s), large={Large}, tar={Tar}", filesByChunkHash.Count, largeChunks, tarChunks);
            await _mediator.Publish(new ChunkResolutionCompleteEvent(filesByChunkHash.Count, largeChunks, tarChunks, totalOriginalBytes, totalCompressedBytes), cancellationToken);

            // ── Step 5: Rehydration status check ──────────────────────────────

            var available          = new List<string>();   // chunk hashes ready to download
            var rehydrated         = new List<string>();   // chunk hashes in chunks-rehydrated/
            var needsRehydration   = new List<string>();   // archive-tier, not yet rehydrated
            var rehydrationPending = new List<string>();   // copy already in progress

            foreach (var chunkHash in filesByChunkHash.Keys)
            {
                // Check chunks-rehydrated/ first
                var rehydratedName = BlobPaths.ChunkRehydrated(chunkHash);
                var rehydratedMeta = await _blobs.GetMetadataAsync(rehydratedName, cancellationToken);

                if (rehydratedMeta.Exists)
                {
                    if (rehydratedMeta.Tier != BlobTier.Archive)
                    {
                        // Copy completed — blob is ready to download (Hot/Cool/Cold).
                        rehydrated.Add(chunkHash);
                    }
                    else
                    {
                        // Copy is still in progress: Azure creates the destination in Archive
                        // tier while the copy is pending. The blob is not yet downloadable.
                        rehydrationPending.Add(chunkHash);
                    }
                    continue;
                }

                // Check chunks/ tier
                var chunkName = BlobPaths.Chunk(chunkHash);
                var chunkMeta = await _blobs.GetMetadataAsync(chunkName, cancellationToken);
                if (!chunkMeta.Exists)
                {
                    _logger.LogWarning("Chunk blob not found: {ChunkHash}", chunkHash);
                    continue;
                }

                if (chunkMeta.Tier == BlobTier.Archive)
                {
                    if (chunkMeta.IsRehydrating)
                        rehydrationPending.Add(chunkHash);
                    else
                        needsRehydration.Add(chunkHash);
                }
                else
                {
                    available.Add(chunkHash);
                }
            }

            _logger.LogInformation("[rehydration] Status: available={Available} rehydrated={Rehydrated} needsRehydration={NeedsRehydration} pending={Pending}", available.Count, rehydrated.Count, needsRehydration.Count, rehydrationPending.Count);
            await _mediator.Publish(new RehydrationStatusEvent(available.Count, rehydrated.Count, needsRehydration.Count, rehydrationPending.Count), cancellationToken);

            // ── Step 6 (task 10.6): Cost estimation and confirmation ──────────────

            long rehydrationBytes = 0;
            long downloadBytes    = 0;

            foreach (var chunkHash in available.Concat(rehydrated))
            {
                if (indexEntries.TryGetValue(filesByChunkHash[chunkHash][0].ContentHash, out var ie))
                    downloadBytes += ie.CompressedSize;
            }
            foreach (var chunkHash in needsRehydration.Concat(rehydrationPending))
            {
                if (indexEntries.TryGetValue(filesByChunkHash[chunkHash][0].ContentHash, out var ie))
                    rehydrationBytes += ie.CompressedSize;
            }

            // Build cost estimate via the calculator (pricing config loaded from override or embedded default)
            var costEstimate = RestoreCostCalculator.Compute(
                chunksAvailable:          available.Count,
                chunksAlreadyRehydrated:  rehydrated.Count,
                chunksNeedingRehydration: needsRehydration.Count,
                chunksPendingRehydration: rehydrationPending.Count,
                rehydrationBytes:         rehydrationBytes,
                downloadBytes:            downloadBytes);

            // If there are archive-tier chunks, invoke confirmation callback (task 10.6)
            var rehydratePriority = RehydratePriority.Standard;

            if (needsRehydration.Count > 0 || rehydrationPending.Count > 0)
            {
                if (opts.ConfirmRehydration is not null)
                {
                    var chosenPriority = await opts.ConfirmRehydration(costEstimate, cancellationToken);
                    if (chosenPriority is null)
                    {
                        // User cancelled rehydration — exit without downloading or rehydrating
                        return new RestoreResult
                        {
                            Success                  = true,
                            FilesRestored            = 0,
                            FilesSkipped             = skipped,
                            ChunksPendingRehydration = needsRehydration.Count + rehydrationPending.Count,
                        };
                    }
                    rehydratePriority = chosenPriority.Value;
                }
            }

            // ── Step 7: Phase 1 — download available chunks ───────────────────

            const int DownloadWorkers = 4;

            // Build a hashset for O(1) rehydrated lookup
            var rehydratedSet = new HashSet<string>(rehydrated, StringComparer.Ordinal);

            // Download both directly-available and already-rehydrated chunks in parallel
            await Parallel.ForEachAsync(
                available.Concat(rehydrated),
                new ParallelOptions { MaxDegreeOfParallelism = DownloadWorkers, CancellationToken = cancellationToken },
                async (chunkHash, ct) =>
                {
                    var isRehydrated = rehydratedSet.Contains(chunkHash);
                    var blobName = isRehydrated
                        ? BlobPaths.ChunkRehydrated(chunkHash)
                        : BlobPaths.Chunk(chunkHash);

                    var filesForChunk = filesByChunkHash[chunkHash];

                    // Determine chunk type from index entry
                    // If content-hash == chunk-hash → large file
                    // otherwise → thin/tar bundle
                    var firstFile   = filesForChunk[0];
                    if (!indexEntries.TryGetValue(firstFile.ContentHash, out var indexEntry))
                        return;

                    bool isLargeChunk = indexEntry.ContentHash == indexEntry.ChunkHash;

                    // For large files, sizes come from the single index entry.
                    // For tar bundles, ShardEntry.CompressedSize is a proportional per-file share;
                    // sum across all files to reconstruct the total tar.gz blob size.
                    var compressedSize = isLargeChunk
                        ? indexEntry.CompressedSize
                        : filesForChunk.Sum(f => indexEntries.TryGetValue(f.ContentHash, out var e) ? e.CompressedSize : 0);
                    var originalSize = isLargeChunk
                        ? indexEntry.OriginalSize
                        : filesForChunk.Sum(f => indexEntries.TryGetValue(f.ContentHash, out var e) ? e.OriginalSize : 0);

                    _logger.LogInformation("[download] Chunk {ChunkHash} ({Type}, {FileCount} file(s), compressed={Compressed})", chunkHash[..8], isLargeChunk ? "large" : "tar", filesForChunk.Count, compressedSize.Bytes().Humanize());
                    await _mediator.Publish(new ChunkDownloadStartedEvent(chunkHash, isLargeChunk ? "large" : "tar", filesForChunk.Count, compressedSize, originalSize), ct);

                    if (isLargeChunk)
                    {
                        // Large file: single file maps to this chunk
                        var file = filesForChunk[0]; // only one file per large chunk
                        await RestoreLargeFileAsync(blobName, file, opts, compressedSize, ct);
                        Interlocked.Increment(ref filesRestoredLong);
                        await _mediator.Publish(new FileRestoredEvent(file.RelativePath, indexEntry.OriginalSize), ct);
                    }
                    else
                    {
                        // Tar bundle: stream through tar, extract matching entries.
                        // Multiple files may share the same content hash (duplicates), so use a lookup.
                        var filesByContentHash = filesForChunk
                            .GroupBy(f => f.ContentHash, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                        var restored = await RestoreTarBundleAsync(
                            blobName, chunkHash, filesByContentHash, opts, compressedSize, ct);
                        Interlocked.Add(ref filesRestoredLong, restored);
                    }
                });

            filesRestored = (int)Interlocked.Read(ref filesRestoredLong);

            // ── Step 8: Phase 2 — kick off rehydration (task 10.8) ───────────────

            // Only request rehydration for chunks that have NOT yet been requested.
            // Chunks in rehydrationPending already have a copy in progress; re-requesting
            // would throw BlobArchived 409 from Azure because StartCopyFromUri is not
            // permitted on an archived blob that already has a pending copy.
            var chunksToRequest = needsRehydration.ToList();
            int chunksToRehydrate = chunksToRequest.Count + rehydrationPending.Count;

            if (chunksToRehydrate > 0)
            {
                long totalRehydrateBytes = 0;
                foreach (var chunkHash in chunksToRequest)
                {
                    var chunkName = BlobPaths.Chunk(chunkHash);
                    var dst       = BlobPaths.ChunkRehydrated(chunkHash);
                    try
                    {
                        await _blobs.CopyAsync(chunkName, dst, BlobTier.Cold, rehydratePriority, cancellationToken);

                        if (indexEntries.TryGetValue(filesByChunkHash[chunkHash][0].ContentHash, out var ie))
                            totalRehydrateBytes += ie.CompressedSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to start rehydration for chunk {ChunkHash}", chunkHash);
                    }
                }

                await _mediator.Publish(new RehydrationStartedEvent(chunksToRehydrate, totalRehydrateBytes), cancellationToken);
            }

            totalPending = chunksToRehydrate;

            } // end if (toRestore.Count > 0)

            // ── Step 9 (task 10.10): Cleanup ALL rehydrated blobs in the container ─

            if (totalPending == 0)
            {
                // Enumerate ALL blobs under chunks-rehydrated/, not just those used by this restore.
                var allRehydratedBlobs = new List<string>();
                await foreach (var blobName in _blobs.ListAsync(BlobPaths.ChunksRehydrated, cancellationToken))
                    allRehydratedBlobs.Add(blobName);

                if (allRehydratedBlobs.Count > 0)
                {
                    long totalRehydratedBytes = 0;
                    foreach (var blobName in allRehydratedBlobs)
                    {
                        try
                        {
                            var meta = await _blobs.GetMetadataAsync(blobName, cancellationToken);
                            totalRehydratedBytes += meta.ContentLength ?? 0;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get metadata for rehydrated blob {BlobName}", blobName);
                        }
                    }

                    if (opts.ConfirmCleanup is not null && await opts.ConfirmCleanup(allRehydratedBlobs.Count, totalRehydratedBytes, cancellationToken))
                    {
                        int chunksDeleted = 0;
                        long bytesFreed   = 0;

                        foreach (var blobName in allRehydratedBlobs)
                        {
                            try
                            {
                                var meta = await _blobs.GetMetadataAsync(blobName, cancellationToken);
                                await _blobs.DeleteAsync(blobName, cancellationToken);
                                chunksDeleted++;
                                bytesFreed += meta.ContentLength ?? 0;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete rehydrated blob {BlobName}", blobName);
                            }
                        }

                        _logger.LogInformation("[cleanup] Deleted {ChunksDeleted} rehydrated blob(s), freed {BytesFreed} bytes", chunksDeleted, bytesFreed);
                        await _mediator.Publish(new CleanupCompleteEvent(chunksDeleted, bytesFreed), cancellationToken);
                    }
                }
            }

            _logger.LogInformation("[restore] Done: restored={Restored} skipped={Skipped} pendingRehydration={Pending}", filesRestored, skipped, totalPending);

            return new RestoreResult
            {
                Success                  = true,
                FilesRestored            = filesRestored,
                FilesSkipped             = skipped,
                ChunksPendingRehydration = totalPending,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore pipeline failed");
            return new RestoreResult
            {
                Success             = false,
                FilesRestored       = 0,
                FilesSkipped        = 0,
                ChunksPendingRehydration = 0,
                ErrorMessage        = ex.Message
            };
        }
    }

    // ── Tree traversal (task 10.2) ────────────────────────────────────────────

    /// <summary>
    /// Walks the Merkle tree from <paramref name="rootHash"/> and collects all file entries
    /// that match <paramref name="targetPath"/> (or all files if <c>null</c>).
    /// Emits batched <see cref="TreeTraversalProgressEvent"/> during traversal.
    /// </summary>
    private async Task<List<FileToRestore>> CollectFilesAsync(string rootHash, string? targetPath, TreeBuilder treeCache, CancellationToken cancellationToken)
    {
        var result = new List<FileToRestore>();
        var prefix = NormalizePath(targetPath);

        var lastEmit = DateTimeOffset.UtcNow;
        var lastEmitCount = 0;

        await WalkTreeAsync(rootHash, string.Empty, prefix, result, cancellationToken, async () =>
        {
            // Emit progress event every 10 files or every 100ms
            var now = DateTimeOffset.UtcNow;
            if (result.Count - lastEmitCount >= 10 || (now - lastEmit).TotalMilliseconds >= 100)
            {
                lastEmitCount = result.Count;
                lastEmit = now;
                _logger.LogDebug("[tree] Traversal progress: {FilesFound} files discovered", result.Count);
                await _mediator.Publish(new TreeTraversalProgressEvent(result.Count), cancellationToken);
            }
        });

        // Emit final progress event to ensure the last count is reported
        if (result.Count > lastEmitCount)
        {
            _logger.LogDebug("[tree] Traversal progress: {FilesFound} files discovered", result.Count);
            await _mediator.Publish(new TreeTraversalProgressEvent(result.Count), cancellationToken);
        }

        return result;
    }

    private async Task WalkTreeAsync(
        string             treeHash,
        string             currentPath,     // forward-slash relative, no trailing slash
        string?            targetPrefix,
        List<FileToRestore> result,
        CancellationToken  cancellationToken,
        Func<Task>?        onFileDiscovered = null)
    {
        // Skip entire subtrees that cannot match the prefix filter
        if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix))
            return;

        // Load tree blob from cache/storage
        var blobName = BlobPaths.FileTree(treeHash);
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption, cancellationToken);

        foreach (var entry in treeBlob.Entries)
        {
            var entryPath = currentPath.Length == 0
                ? entry.Name
                : $"{currentPath}/{entry.Name}";

            if (entry.Type == TreeEntryType.Dir)
            {
                // Strip trailing slash from directory name used in path assembly
                var dirPath = entryPath.TrimEnd('/');
                await WalkTreeAsync(entry.Hash, dirPath, targetPrefix, result, cancellationToken, onFileDiscovered);
            }
            else
            {
                // File entry
                if (targetPrefix is null || entryPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new FileToRestore(
                        RelativePath : entryPath,
                        ContentHash  : entry.Hash,
                        Created      : entry.Created  ?? DateTimeOffset.UtcNow,
                        Modified     : entry.Modified ?? DateTimeOffset.UtcNow));

                    if (onFileDiscovered is not null)
                        await onFileDiscovered();
                }
            }
        }
    }

    private static bool IsPathRelevant(string currentPath, string targetPrefix)
    {
        if (currentPath.Length == 0) return true; // root always relevant

        // currentPath is a directory; it's relevant if either:
        //   (a) it's a prefix of targetPrefix (we need to descend into it)
        //   (b) targetPrefix is a prefix of it (it's inside the target dir)
        return targetPrefix.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Replace('\\', '/').TrimEnd('/');
    }

    // ── Large file restore (task 10.7) ────────────────────────────────────────

    private async Task RestoreLargeFileAsync(
        string         blobName,
        FileToRestore  file,
        RestoreOptions opts,
        long           compressedSize,
        CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(opts.RootDirectory,
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        // Streaming: download → (progress) → decrypt → gunzip → write
        // All streams must be closed before setting timestamps, otherwise the
        // FileStream disposal resets LastWriteTimeUtc to "now" on some platforms.
        {
            await using var downloadStream = await _blobs.DownloadAsync(blobName, cancellationToken);

            // Wrap with ProgressStream if progress callback is provided
            Stream progressOrRawStream = downloadStream;
            if (opts.CreateDownloadProgress is not null)
            {
                var progress = opts.CreateDownloadProgress(file.RelativePath, compressedSize, DownloadKind.LargeFile);
                progressOrRawStream = new ProgressStream(downloadStream, progress);
            }

            await using var _ = progressOrRawStream == downloadStream ? null : progressOrRawStream as IAsyncDisposable;
            await using var decryptStream  = _encryption.WrapForDecryption(progressOrRawStream);
            await using var gzipStream     = new GZipStream(decryptStream, CompressionMode.Decompress);
            await using var outputStream   = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            await gzipStream.CopyToAsync(outputStream, cancellationToken);
        }

        // Set file timestamps from tree metadata (after stream is closed)
        File.SetCreationTimeUtc(localPath,  file.Created.UtcDateTime);
        File.SetLastWriteTimeUtc(localPath, file.Modified.UtcDateTime);

        // Create pointer file (task 10.11)
        if (!opts.NoPointers)
        {
            var pointerPath = localPath + ".pointer.arius";
            await File.WriteAllTextAsync(pointerPath, file.ContentHash, cancellationToken);
            File.SetCreationTimeUtc(pointerPath,  file.Created.UtcDateTime);
            File.SetLastWriteTimeUtc(pointerPath, file.Modified.UtcDateTime);
        }
    }

    // ── Tar bundle restore (task 10.7) ────────────────────────────────────────

    /// <summary>
    /// Downloads a tar bundle and extracts only the files whose content-hash matches
    /// an entry in <paramref name="filesNeeded"/>.
    /// <summary>
    /// Extracts files from a gzip-compressed tar bundle blob and restores only entries whose tar entry names (content hashes) are present in <paramref name="filesNeeded"/>.
    /// </summary>
    /// <param name="blobName">The blob path of the tar.gz bundle to download and extract.</param>
    /// <param name="chunkHash">The chunk hash of the tar bundle (used for progress tracking and events).</param>
    /// <param name="filesNeeded">Mapping from content-hash (tar entry name) to the list of files that should be restored from that content.</param>
    /// <param name="opts">Restore options that provide the target root directory and pointer-file behavior.</param>
    /// <param name="compressedSize">The compressed size of the tar bundle.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The number of files written to disk.</returns>
    private async Task<int> RestoreTarBundleAsync(
        string                                    blobName,
        string                                    chunkHash,
        Dictionary<string, List<FileToRestore>>   filesNeeded,
        RestoreOptions                            opts,
        long                                      compressedSize,
        CancellationToken                         cancellationToken)
    {
        int restored = 0;

        await using var downloadStream = await _blobs.DownloadAsync(blobName, cancellationToken);

        // Wrap with ProgressStream if progress callback is provided
        Stream progressOrRawStream = downloadStream;
        if (opts.CreateDownloadProgress is not null)
        {
            var progress = opts.CreateDownloadProgress(chunkHash, compressedSize, DownloadKind.TarBundle);
            progressOrRawStream = new ProgressStream(downloadStream, progress);
        }

        await using var _ = progressOrRawStream == downloadStream ? null : progressOrRawStream as IAsyncDisposable;
        await using var decryptStream  = _encryption.WrapForDecryption(progressOrRawStream);
        await using var gzipStream     = new GZipStream(decryptStream, CompressionMode.Decompress);
        var tarReader = new TarReader(gzipStream, leaveOpen: false);

        TarEntry? tarEntry;
        while ((tarEntry = await tarReader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            var contentHash = tarEntry.Name; // entries are named by content-hash

            if (!filesNeeded.TryGetValue(contentHash, out var filesForHash))
                continue; // not needed for this restore — skip

            // Buffer the entry data once (multiple output paths may share same content)
            byte[]? dataBuffer = null;
            if (tarEntry.DataStream is not null)
            {
                using var ms = new MemoryStream();
                await tarEntry.DataStream.CopyToAsync(ms, cancellationToken);
                dataBuffer = ms.ToArray();
            }

            foreach (var file in filesForHash)
            {
                var localPath = Path.Combine(opts.RootDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                if (dataBuffer is not null)
                {
                    await File.WriteAllBytesAsync(localPath, dataBuffer, cancellationToken);
                }
                else
                {
                    // Empty file (0 bytes): create an empty file
                    await using var __ = File.Create(localPath);
                }

                // Set timestamps
                File.SetCreationTimeUtc(localPath,  file.Created.UtcDateTime);
                File.SetLastWriteTimeUtc(localPath, file.Modified.UtcDateTime);

                // Create pointer file (task 10.11)
                if (!opts.NoPointers)
                {
                    var pointerPath = localPath + ".pointer.arius";
                    await File.WriteAllTextAsync(pointerPath, contentHash, cancellationToken);
                    File.SetCreationTimeUtc(pointerPath,  file.Created.UtcDateTime);
                    File.SetLastWriteTimeUtc(pointerPath, file.Modified.UtcDateTime);
                }

                await _mediator.Publish(new FileRestoredEvent(file.RelativePath, dataBuffer?.Length ?? 0), cancellationToken);
                restored++;
            }
        }

        // Emit completion event for tar bundles so CLI can remove TrackedDownload
        await _mediator.Publish(new ChunkDownloadCompletedEvent(chunkHash, restored, compressedSize), cancellationToken);

        return restored;
    }
}