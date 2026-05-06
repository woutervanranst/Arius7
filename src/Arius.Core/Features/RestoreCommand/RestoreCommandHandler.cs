using System.Formats.Tar;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;
using ChunkHydrationStatus = Arius.Core.Shared.ChunkStorage.ChunkHydrationStatus;

namespace Arius.Core.Features.RestoreCommand;

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
public sealed class RestoreCommandHandler
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    private readonly IEncryptionService             _encryption;
    private readonly ChunkIndexService              _index;
    private readonly IChunkStorageService           _chunkStorage;
    private readonly FileTreeService                _fileTreeService;
    private readonly SnapshotService                _snapshotSvc;
    private readonly IMediator                      _mediator;
    private readonly ILogger<RestoreCommandHandler> _logger;
    private readonly string                         _accountName;
    private readonly string                         _containerName;

    public RestoreCommandHandler(
        IEncryptionService             encryption,
        ChunkIndexService              index,
        IChunkStorageService           chunkStorage,
        FileTreeService                fileTreeService,
        SnapshotService                snapshotSvc,
        IMediator                      mediator,
        ILogger<RestoreCommandHandler> logger,
        string                         accountName,
        string                         containerName)
    {
        _encryption      = encryption;
        _index           = index;
        _chunkStorage    = chunkStorage;
        _fileTreeService = fileTreeService;
        _snapshotSvc     = snapshotSvc;
        _mediator        = mediator;
        _logger          = logger;
        _accountName     = accountName;
        _containerName   = containerName;
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

        try
        {
            // ── Step 1: Resolve snapshot ──────────────────────────────────────

            var snapshot    = await _snapshotSvc.ResolveAsync(opts.Version, cancellationToken);

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

            _logger.LogInformation("[snapshot] Resolved: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

            // ── Step 2: Tree traversal ────────────────────────────────────────

            var files     = await CollectFilesAsync(snapshot.RootHash, opts.TargetPath, cancellationToken);

            _logger.LogInformation("[tree] Traversal complete: {Count} file(s) collected", files.Count);

            // Publish snapshot resolved after tree traversal so we include file count (from tree)
            await _mediator.Publish(new SnapshotResolvedEvent(snapshot.Timestamp, snapshot.RootHash, files.Count), cancellationToken);
            await _mediator.Publish(new TreeTraversalCompleteEvent(files.Count, TotalOriginalSize: 0), cancellationToken);
            await _mediator.Publish(new RestoreStartedEvent(files.Count), cancellationToken);

            // ── Step 3: Conflict check ────────────────────────────────────────

            var toRestore = new List<FileToRestore>();
            var skipped   = 0;

            foreach (var file in files)
            {
                var localPath = opts.RootDirectory / file.RelativePath;

                if (localPath.ExistsFile)
                {
                    if (!opts.Overwrite)
                    {
                        // Hash local file to check if already correct
                        await using var fs = localPath.OpenRead();
                        var localHash = await _encryption.ComputeHashAsync(fs, cancellationToken);

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
                        await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.Overwrite, localPath.Length), cancellationToken);
                    }
                }
                else
                {
                    _logger.LogInformation("[disposition] {Path} -> new", file.RelativePath);
                    await _mediator.Publish(new FileDispositionEvent(file.RelativePath, RestoreDisposition.New, 0), cancellationToken);
                }

                toRestore.Add(file);
            }

            var filesRestored    = 0;
            long filesRestoredLong = 0;
            var totalPending     = 0;

            if (toRestore.Count > 0)
            {

            // ── Step 4: Chunk resolution ──────────────────────────────────────

            var contentHashes = toRestore
                .Select(file => file.ContentHash)
                .Distinct()
                .ToList();
            var indexEntries  = await _index.LookupAsync(contentHashes, cancellationToken);

            // Group files by chunk hash
            var filesByChunkHash = new Dictionary<ChunkHash, List<FileToRestore>>();
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

            var largeChunks = filesByChunkHash.Keys.Count(k => indexEntries.TryGetValue(filesByChunkHash[k][0].ContentHash, out var entry) && entry.IsLargeChunk);
            var tarChunks   = filesByChunkHash.Count - largeChunks;

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
                    if (ie2.IsLargeChunk)
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

            var available          = new List<ChunkHash>();   // chunk hashes ready to download
            var needsRehydration   = new List<ChunkHash>();   // archive-tier, not yet rehydrated
            var rehydrationPending = new List<ChunkHash>();   // copy already in progress

            foreach (var chunkHash in filesByChunkHash.Keys)
            {
                var hydrationStatus = await _chunkStorage.GetHydrationStatusAsync(chunkHash, cancellationToken);
                switch (hydrationStatus)
                {
                    case ChunkHydrationStatus.Unknown:
                        _logger.LogWarning("Chunk blob not found: {ChunkHash}", chunkHash);
                        break;
                    case ChunkHydrationStatus.RehydrationPending:
                        rehydrationPending.Add(chunkHash);
                        break;
                    case ChunkHydrationStatus.NeedsRehydration:
                        needsRehydration.Add(chunkHash);
                        break;
                    case ChunkHydrationStatus.Available:
                        available.Add(chunkHash);
                        break;
                }
            }

            _logger.LogInformation("[rehydration] Status: available={Available} rehydrated={Rehydrated} needsRehydration={NeedsRehydration} pending={Pending}", available.Count, 0, needsRehydration.Count, rehydrationPending.Count);
            await _mediator.Publish(new RehydrationStatusEvent(available.Count, 0, needsRehydration.Count, rehydrationPending.Count), cancellationToken);

            // ── Step 6 (task 10.6): Cost estimation and confirmation ──────────────

            long rehydrationBytes = 0;
            long downloadBytes    = 0;

            foreach (var chunkHash in available)
                downloadBytes += SumCompressedBytes(chunkHash);
            foreach (var chunkHash in needsRehydration.Concat(rehydrationPending))
                rehydrationBytes += SumCompressedBytes(chunkHash);

            long SumCompressedBytes(ChunkHash chunkHash)
            {
                var firstFile = filesByChunkHash[chunkHash][0];
                if (!indexEntries.TryGetValue(firstFile.ContentHash, out var ie))
                    return 0;

                var isLargeChunk = ie.IsLargeChunk;
                if (isLargeChunk)
                    return ie.CompressedSize;

                // Tar bundle: sum proportional shares across all files in the chunk
                long sum = 0;
                foreach (var file in filesByChunkHash[chunkHash])
                {
                    if (indexEntries.TryGetValue(file.ContentHash, out var fileEntry))
                        sum += fileEntry.CompressedSize;
                }
                return sum;
            }

            // Build cost estimate via the calculator (pricing config loaded from override or embedded default)
            var costEstimate = RestoreCostCalculator.Compute(
                chunksAvailable:          available.Count,
                chunksAlreadyRehydrated:  0,
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

            // Download available chunks in parallel
            await Parallel.ForEachAsync(
                available,
                new ParallelOptions { MaxDegreeOfParallelism = DownloadWorkers, CancellationToken = cancellationToken },
                async (chunkHash, ct) =>
                {
                    var filesForChunk = filesByChunkHash[chunkHash];

                    // Determine chunk type from index entry
                    // If content-hash == chunk-hash → large file
                    // otherwise → thin/tar bundle
                    var firstFile   = filesForChunk[0];
                    if (!indexEntries.TryGetValue(firstFile.ContentHash, out var indexEntry))
                        return;

                    var isLargeChunk = indexEntry.IsLargeChunk;

                    // For large files, sizes come from the single index entry.
                    // For tar bundles, ShardEntry.CompressedSize is a proportional per-file share;
                    // sum across all files to reconstruct the total tar.gz blob size.
                    var compressedSize = isLargeChunk
                        ? indexEntry.CompressedSize
                        : filesForChunk.Sum(f => indexEntries.TryGetValue(f.ContentHash, out var e) ? e.CompressedSize : 0);
                    var originalSize = isLargeChunk
                        ? indexEntry.OriginalSize
                        : filesForChunk.Sum(f => indexEntries.TryGetValue(f.ContentHash, out var e) ? e.OriginalSize : 0);

                    _logger.LogInformation("[download] Chunk {ChunkHash} ({Type}, {FileCount} file(s), compressed={Compressed})", chunkHash.Short8, isLargeChunk ? "large" : "tar", filesForChunk.Count, compressedSize.Bytes().Humanize());
                    await _mediator.Publish(new ChunkDownloadStartedEvent(chunkHash, isLargeChunk ? "large" : "tar", filesForChunk.Count, compressedSize, originalSize), ct);

                    if (isLargeChunk)
                    {
                        foreach (var file in filesForChunk)
                        {
                            await RestoreLargeFileAsync(chunkHash, file, opts, compressedSize, ct);
                            Interlocked.Increment(ref filesRestoredLong);
                            await _mediator.Publish(new FileRestoredEvent(file.RelativePath, indexEntry.OriginalSize), ct);
                        }
                    }
                    else
                    {
                        // Tar bundle: stream through tar, extract matching entries.
                        // Multiple files may share the same content hash (duplicates), so use a lookup.
                        var filesByContentHash = filesForChunk
                            .GroupBy(f => f.ContentHash)
                            .ToDictionary(g => g.Key, g => g.ToList());
                        var restored = await RestoreTarBundleAsync(
                            chunkHash, filesByContentHash, opts, compressedSize, ct);
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
            var chunksToRehydrate = chunksToRequest.Count + rehydrationPending.Count;

            if (chunksToRehydrate > 0)
            {
                long totalRehydrateBytes = 0;
                foreach (var chunkHash in chunksToRequest)
                {
                    try
                    {
                        await _chunkStorage.StartRehydrationAsync(chunkHash, rehydratePriority, cancellationToken);

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
                await using var cleanupPlan = await _chunkStorage.PlanRehydratedCleanupAsync(cancellationToken);
                if (cleanupPlan.ChunkCount > 0)
                {
                    if (opts.ConfirmCleanup is not null && await opts.ConfirmCleanup(cleanupPlan.ChunkCount, cleanupPlan.TotalBytes, cancellationToken))
                    {
                        var cleanupResult = await cleanupPlan.ExecuteAsync(cancellationToken);
                        _logger.LogInformation("[cleanup] Deleted {ChunksDeleted} rehydrated blob(s), freed {BytesFreed} bytes", cleanupResult.DeletedChunkCount, cleanupResult.FreedBytes);
                        await _mediator.Publish(new CleanupCompleteEvent(cleanupResult.DeletedChunkCount, cleanupResult.FreedBytes), cancellationToken);
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
    private async Task<List<FileToRestore>> CollectFilesAsync(FileTreeHash rootHash, RelativePath? targetPath, CancellationToken cancellationToken)
    {
        var result = new List<FileToRestore>();

        var lastEmit = DateTimeOffset.UtcNow;
        var lastEmitCount = 0;

        await WalkTreeAsync(rootHash, RelativePath.Root, targetPath, result, cancellationToken, async () =>
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
        FileTreeHash        treeHash,
        RelativePath        currentPath,
        RelativePath?       targetPrefix,
        List<FileToRestore> result,
        CancellationToken   cancellationToken,
        Func<Task>?         onFileDiscovered = null)
    {
        // Skip entire subtrees that cannot match the prefix filter
        if (targetPrefix is { } resolvedTargetPrefix && !IsPathRelevant(currentPath, resolvedTargetPrefix))
            return;

        // Load tree entries via cache
        var treeEntries = await _fileTreeService.ReadAsync(treeHash, cancellationToken);

        foreach (var entry in treeEntries)
        {
            if (entry is DirectoryEntry directoryEntry)
            {
                var dirPath = currentPath / directoryEntry.Name;
                await WalkTreeAsync(directoryEntry.FileTreeHash, dirPath, targetPrefix, result, cancellationToken, onFileDiscovered);
            }
            else if (entry is FileEntry fileEntry)
            {
                var filePath = currentPath / fileEntry.Name;

                if (targetPrefix is null || filePath.StartsWith(targetPrefix.Value))
                {
                    result.Add(new FileToRestore(
                        RelativePath : filePath,
                        ContentHash  : fileEntry.ContentHash,
                        Created      : fileEntry.Created,
                        Modified     : fileEntry.Modified));

                    if (onFileDiscovered is not null)
                        await onFileDiscovered();
                }
            }
        }
    }

    private static bool IsPathRelevant(RelativePath currentPath, RelativePath targetPrefix)
    {
        return currentPath.IsRoot
            || targetPrefix.StartsWith(currentPath)
            || currentPath.StartsWith(targetPrefix);
    }

    // ── Large file restore (task 10.7) ────────────────────────────────────────

    private async Task RestoreLargeFileAsync(
        ChunkHash      chunkHash,
        FileToRestore  file,
        RestoreOptions opts,
        long           compressedSize,
        CancellationToken cancellationToken)
    {
        var localPath = opts.RootDirectory / file.RelativePath;

        RootedPath? localDirectory = localPath.RelativePath.Parent is { } localParent
            ? localParent.RootedAt(localPath.Root)
            : null;
        localDirectory?.CreateDirectory();

        {
            var progress = opts.CreateDownloadProgress?.Invoke(file.RelativePath.ToString(), compressedSize, DownloadKind.LargeFile);
            await using var payloadStream = await _chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
            await using var fileStream   = localPath.OpenWrite();

            await payloadStream.CopyToAsync(fileStream, cancellationToken);
        }

        // Set file timestamps from tree metadata (after stream is closed)
        localPath.CreationTimeUtc = file.Created.UtcDateTime;
        localPath.LastWriteTimeUtc = file.Modified.UtcDateTime;

        // Create pointer file (task 10.11)
        if (!opts.NoPointers)
        {
            var pointerPath = file.RelativePath.ToPointerFilePath().RootedAt(opts.RootDirectory);
            await pointerPath.WriteAllTextAsync(file.ContentHash.ToString(), cancellationToken);
            pointerPath.CreationTimeUtc = file.Created.UtcDateTime;
            pointerPath.LastWriteTimeUtc = file.Modified.UtcDateTime;
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
        ChunkHash                                 chunkHash,
        Dictionary<ContentHash, List<FileToRestore>> filesNeeded,
        RestoreOptions                            opts,
        long                                      compressedSize,
        CancellationToken                         cancellationToken)
    {
        var restored = 0;

        var progress = opts.CreateDownloadProgress?.Invoke(chunkHash.ToString(), compressedSize, DownloadKind.TarBundle);
        await using var payloadStream = await _chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
        var tarReader = new TarReader(payloadStream, leaveOpen: false);

        while (await tarReader.GetNextEntryAsync(copyData: false, cancellationToken) is { } tarEntry)
        {
            if (!ContentHash.TryParse(tarEntry.Name, out var contentHash))
                throw new FormatException($"Invalid tar entry name '{tarEntry.Name}' in chunk '{chunkHash}'.");

            if (!filesNeeded.TryGetValue(contentHash, out var filesForHash))
                continue; // not needed for this restore — skip

            RootedPath? sourcePath = null;

            for (var i = 0; i < filesForHash.Count; i++)
            {
                var file = filesForHash[i];
                var localPath = opts.RootDirectory / file.RelativePath;

                RootedPath? localDirectory = localPath.RelativePath.Parent is { } localParent
                    ? localParent.RootedAt(localPath.Root)
                    : null;
                localDirectory?.CreateDirectory();

                if (tarEntry.DataStream is null)
                {
                    // create an empty file
                    await using var _ = localPath.OpenWrite();
                }
                else if (i == 0)
                {
                    await using var output = localPath.OpenWrite();
                    await tarEntry.DataStream.CopyToAsync(output, cancellationToken);
                    sourcePath = localPath;
                }
                else
                {
                    await sourcePath!.Value.CopyToAsync(localPath, overwrite: true, cancellationToken);
                }

                // Set timestamps
                localPath.CreationTimeUtc = file.Created.UtcDateTime;
                localPath.LastWriteTimeUtc = file.Modified.UtcDateTime;

                // Create pointer file
                if (!opts.NoPointers)
                {
                    var pointerPath = file.RelativePath.ToPointerFilePath().RootedAt(opts.RootDirectory);
                    await pointerPath.WriteAllTextAsync(contentHash.ToString(), cancellationToken);
                    pointerPath.CreationTimeUtc = file.Created.UtcDateTime;
                    pointerPath.LastWriteTimeUtc = file.Modified.UtcDateTime;
                }

                await _mediator.Publish(new FileRestoredEvent(file.RelativePath, localPath.Length), cancellationToken);
                restored++;
            }
        }

        // Emit completion event for tar bundles so CLI can remove TrackedDownload
        await _mediator.Publish(new ChunkDownloadCompletedEvent(chunkHash, restored, compressedSize), cancellationToken);

        return restored;
    }
}
