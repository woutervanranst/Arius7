using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Extensions;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Orchestrates restore from a repository snapshot into a local directory.
/// The handler owns snapshot selection, chunk classification, confirmation, download, rehydration,
/// and cleanup. <see cref="RestoreFilePipeline"/> owns the shared Walk -> Route -> Resolve file stream
/// used by both restore passes.
///
/// Restore must classify archive-tier chunks and ask for rehydration confirmation <em>before</em> any
/// download starts, so cancellation never writes files. The repository tree is therefore walked twice;
/// <see cref="IFileTreeService.ReadAsync"/> is cache-backed, so the second walk is cheap.
///
/// ## Stages
///
/// 1. **Resolve snapshot** - choose the requested snapshot, or the latest snapshot when no version is supplied.
/// 2. **Classify** (walk #1) - run Walk -> Route -> Resolve and build one <see cref="ChunkClassification"/>
///    per distinct chunk. Memory is O(distinct chunks), not O(files). Rehydration state comes from one
///    rehydrated-prefix listing plus each chunk's index <c>StorageTierHint</c>; there are no per-chunk
///    storage calls.
/// 3. **Confirm** - publish the cost estimate and invoke <see cref="RestoreOptions.ConfirmRehydration"/>.
///    Cancellation exits before any download starts.
/// 4. **Download** (walk #2, events suppressed) - group routed files by chunk and download each available
///    chunk once. Large chunks restore one file immediately; tar chunks restore after all selected files
///    for the chunk are known. A chunk that is still archived at download time is re-routed to rehydration.
/// 5. **Rehydrate** - request rehydration for archive-tier chunks, skipping chunks already pending.
/// 6. **Cleanup** - when nothing is pending, optionally delete leftover rehydrated chunk blobs.
///
/// ## Pipeline (shared by stages 2 &amp; 4)
///
/// <see cref="RestoreFilePipeline"/> composes breadth-first Walk, parallel Route conflict checks, and
/// batched Resolve chunk-index lookups as one <see cref="IAsyncEnumerable{T}"/> of <see cref="ResolvedFile"/>.
/// The classify pass emits route/progress events; the download pass suppresses them.
///
/// ## Channels
///
/// | Channel        | Writer      | Reader        | Capacity          | Notes                                    |
/// |----------------|-------------|---------------|-------------------|------------------------------------------|
/// | `chunkChannel` | Grouper (1) | Download (×N) | bounded (workers) | Walk #2 only; downloads backpressure it. |
/// </summary>
public sealed class RestoreCommandHandler(
    IEncryptionService encryption,
    IChunkIndexService index,
    IChunkStorageService chunkStorage,
    IFileTreeService fileTreeService,
    ISnapshotService snapshotSvc,
    IMediator mediator,
    ILogger<RestoreCommandHandler> logger,
    string accountName,
    string containerName)
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    // ── Concurrency knobs ──────────────────────────────────────────────────────

    private const int DownloadWorkers = 4;

    /// <summary>
    /// Executes the end-to-end restore pipeline for the provided <see cref="RestoreCommand"/>. See the
    /// type-level documentation for the numbered stage / pipeline / channel breakdown.
    /// </summary>
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        var opts = command.Options;

        logger.LogInformation("[restore] Start: target={RootDir} account={Account} container={Container} version={Version} overwrite={Overwrite}", opts.RootDirectory, accountName, containerName, opts.Version ?? "latest", opts.Overwrite);

        try
        {
            // ── Stage 1: Resolve snapshot ─────────────────────────────────────────
            logger.LogInformation("[phase] resolve-snapshot");
            var snapshot = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
            if (snapshot is null)
            {
                return new RestoreResult
                {
                    Success                  = false,
                    FilesRestored            = 0,
                    FilesSkipped             = 0,
                    ChunksPendingRehydration = 0,
                    ErrorMessage             = opts.Version is null
                        ? "No snapshots found in this repository."
                        : $"Snapshot '{opts.Version}' not found."
                };
            }

            logger.LogInformation("[snapshot] Resolved: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

            // ── Stage 2: Classify (walk #1, streaming + bounded) ──────────────────
            // Stream Walk → Route → Resolve and fold every resolved file into a per-distinct-chunk map.
            // Rehydration state is one prefix listing; each chunk's status is decided from it + tier hint.
            logger.LogInformation("[phase] classify");
            var fs           = new RelativeFileSystem(LocalDirectory.Parse(opts.RootDirectory));
            var filesToRestore = new RestoreFilePipeline(encryption, index, fileTreeService, mediator, logger);
            var rehydratedState = await chunkStorage.ListRehydratedChunksAsync(cancellationToken);
            var skipped         = new StrongBox<long>(0);
            var classification  = new Dictionary<ChunkHash, ChunkClassification>();
            var fileCount       = 0;

            await foreach (var resolved in filesToRestore.GetStreamAsync(fs, snapshot.RootHash, opts, emitEvents: true, skipped, cancellationToken))
            {
                fileCount++;
                var entry     = resolved.IndexEntry;
                var chunkHash = entry.ChunkHash;

                if (!classification.TryGetValue(chunkHash, out var cc))
                {
                    cc = new ChunkClassification
                    {
                        IsLargeChunk = entry.IsLargeChunk,
                        Status       = ClassifyChunk(chunkHash, entry, rehydratedState),
                    };
                    classification[chunkHash] = cc;
                }

                cc.RefCount++;
                if (entry.IsLargeChunk)
                {
                    // Large file: 1:1 chunk, size comes from the single index entry.
                    cc.CompressedSize = entry.CompressedSize;
                    cc.OriginalSize   = entry.OriginalSize;
                }
                else
                {
                    // Tar bundle: ShardEntry sizes are proportional per-file shares; sum across files.
                    cc.CompressedSize += entry.CompressedSize;
                    cc.OriginalSize   += entry.OriginalSize;
                }
            }

            logger.LogInformation("[tree] Traversal complete: {Count} file(s) collected", fileCount);

            // Counts + byte sums from the (bounded) classification map.
            int availableCount = 0, needsCount = 0, pendingCount = 0, largeChunks = 0;
            long totalOriginalBytes = 0, totalCompressedBytes = 0, downloadBytes = 0, rehydrationBytes = 0;
            foreach (var cc in classification.Values)
            {
                if (cc.IsLargeChunk) largeChunks++;
                totalOriginalBytes   += cc.OriginalSize;
                totalCompressedBytes += cc.CompressedSize;
                switch (cc.Status)
                {
                    case ChunkHydrationStatus.Available:          availableCount++; downloadBytes    += cc.CompressedSize; break;
                    case ChunkHydrationStatus.NeedsRehydration:   needsCount++;     rehydrationBytes += cc.CompressedSize; break;
                    case ChunkHydrationStatus.RehydrationPending: pendingCount++;   rehydrationBytes += cc.CompressedSize; break;
                }
            }
            var tarChunks = classification.Count - largeChunks;

            await mediator.Publish(new SnapshotResolvedEvent(snapshot.Timestamp, snapshot.RootHash, fileCount), cancellationToken);
            await mediator.Publish(new TreeTraversalCompleteEvent(fileCount, totalOriginalBytes), cancellationToken);
            await mediator.Publish(new RestoreStartedEvent(fileCount), cancellationToken);

            logger.LogInformation("[chunk] Resolution: {Groups} chunk group(s), large={Large}, tar={Tar}", classification.Count, largeChunks, tarChunks);
            await mediator.Publish(new ChunkResolutionCompleteEvent(classification.Count, largeChunks, tarChunks, totalOriginalBytes, totalCompressedBytes), cancellationToken);

            logger.LogInformation("[rehydration] Status: available={Available} rehydrated={Rehydrated} needsRehydration={NeedsRehydration} pending={Pending}", availableCount, 0, needsCount, pendingCount);
            await mediator.Publish(new RehydrationStatusEvent(availableCount, 0, needsCount, pendingCount), cancellationToken);

            // ── Stage 3: Cost estimate + confirm rehydration ──────────────────────
            var costEstimate = new RestoreCostCalculator(pricing: null).Compute(
                chunksAvailable:          availableCount,
                chunksAlreadyRehydrated:  0,
                chunksNeedingRehydration: needsCount,
                chunksPendingRehydration: pendingCount,
                rehydrationBytes:         rehydrationBytes,
                downloadBytes:            downloadBytes);

            var rehydratePriority = RehydratePriority.Standard;
            if (needsCount > 0 || pendingCount > 0)
            {
                if (opts.ConfirmRehydration is not null)
                {
                    var chosenPriority = await opts.ConfirmRehydration(costEstimate, cancellationToken);
                    if (chosenPriority is null)
                    {
                        // User cancelled rehydration — exit without downloading or rehydrating.
                        return new RestoreResult
                        {
                            Success                  = true,
                            FilesRestored            = 0,
                            FilesSkipped             = (int)skipped.Value,
                            ChunksPendingRehydration = needsCount + pendingCount,
                        };
                    }

                    rehydratePriority = chosenPriority.Value;
                }
            }

            // ── Stage 4: Download available chunks (walk #2, streaming + bounded) ──
            var rerouteToRehydration = new ConcurrentDictionary<ChunkHash, bool>();
            long filesRestored       = 0;

            if (availableCount > 0)
            {
                logger.LogInformation("[phase] download");

                var chunkChannel = Channel.CreateBounded<ChunkToRestore>(new BoundedChannelOptions(DownloadWorkers) { SingleWriter = true, SingleReader = false });
                opts.OnDownloadQueueReady?.Invoke(() => chunkChannel.Reader.Count);

                // All pass-2 work shares this linked token so a download fault (below) can cancel the
                // grouper — otherwise it would block forever writing to the bounded chunk channel that the
                // faulted workers stopped draining.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var ct = cts.Token;

                // ── Grouper ×1: large files dispatch immediately; tar groups flush at refcount ──
                var grouperTask = Task.Run(async () =>
                {
                    var openTars = new Dictionary<ChunkHash, List<FileToRestore>>();
                    try
                    {
                        await foreach (var resolved in filesToRestore.GetStreamAsync(fs, snapshot.RootHash, opts, emitEvents: false, skipped: null, ct))
                        {
                            var chunkHash = resolved.IndexEntry.ChunkHash;
                            if (!classification.TryGetValue(chunkHash, out var cc) || cc.Status != ChunkHydrationStatus.Available)
                                continue; // not downloadable now; rehydration is handled after downloads

                            if (cc.IsLargeChunk)
                            {
                                await chunkChannel.Writer.WriteAsync(new ChunkToRestore(chunkHash, IsLargeChunk: true, [resolved.File], cc.CompressedSize, cc.OriginalSize), ct);
                                continue;
                            }

                            if (!openTars.TryGetValue(chunkHash, out var list))
                                openTars[chunkHash] = list = [];
                            list.Add(resolved.File);

                            // All of this tar's to-restore files have arrived → download it exactly once.
                            if (list.Count >= cc.RefCount)
                            {
                                openTars.Remove(chunkHash);
                                await chunkChannel.Writer.WriteAsync(new ChunkToRestore(chunkHash, IsLargeChunk: false, list, cc.CompressedSize, cc.OriginalSize), ct);
                            }
                        }

                        // Defensive: flush any tar groups that never reached their refcount.
                        foreach (var (chunkHash, list) in openTars)
                        {
                            var cc = classification[chunkHash];
                            await chunkChannel.Writer.WriteAsync(new ChunkToRestore(chunkHash, IsLargeChunk: false, list, cc.CompressedSize, cc.OriginalSize), ct);
                        }

                        chunkChannel.Writer.Complete();
                    }
                    catch (Exception ex)
                    {
                        chunkChannel.Writer.Complete(ex);
                    }
                }, CancellationToken.None);

                // ── Download workers ×N ──
                var downloadTask = Parallel.ForEachAsync(
                    chunkChannel.Reader.ReadAllAsync(ct),
                    new ParallelOptions { MaxDegreeOfParallelism = DownloadWorkers, CancellationToken = ct },
                    async (chunk, ct) =>
                    {
                        logger.LogInformation("[download] Chunk {ChunkHash} ({Type}, {FileCount} file(s), compressed={Compressed})", chunk.ChunkHash.Short8, chunk.IsLargeChunk ? "large" : "tar", chunk.Files.Count, chunk.CompressedSize.Bytes().Humanize());

                        // Publish the start event before invoking the tar progress factory: the CLI's
                        // ChunkDownloadStarted handler populates the metadata that CreateTarBundleDownloadProgress reads.
                        await mediator.Publish(new ChunkDownloadStartedEvent(chunk.ChunkHash, chunk.IsLargeChunk ? "large" : "tar", chunk.Files.Count, chunk.CompressedSize, chunk.OriginalSize), ct);

                        try
                        {
                            if (chunk.IsLargeChunk)
                            {
                                foreach (var file in chunk.Files)
                                {
                                    await RestoreLargeFileAsync(chunk.ChunkHash, file, fs, opts, chunk.CompressedSize, ct);
                                    Interlocked.Increment(ref filesRestored);
                                    await mediator.Publish(new FileRestoredEvent(file.RelativePath, chunk.OriginalSize), ct);
                                }
                            }
                            else
                            {
                                // Multiple files may share the same content hash (duplicates), so use a lookup.
                                var filesByContentHash = chunk.Files
                                    .GroupBy(f => f.ContentHash)
                                    .ToDictionary(g => g.Key, g => g.ToList());
                                var restored = await RestoreTarBundleAsync(chunk.ChunkHash, filesByContentHash, fs, opts, chunk.CompressedSize, ct);
                                Interlocked.Add(ref filesRestored, restored);
                            }
                        }
                        catch (BlobArchivedException) when (!ct.IsCancellationRequested)
                        {
                            // Stale classification (e.g. external tier change): the blob is still archived.
                            // Re-route to the rehydration path rather than faulting the run.
                            logger.LogWarning("[download] Chunk {ChunkHash} is archived despite classification; re-routing to rehydration", chunk.ChunkHash.Short8);
                            rerouteToRehydration.TryAdd(chunk.ChunkHash, true);
                        }
                    });

                try
                {
                    // Await downloads first: a worker fault surfaces here. In the success path the grouper has
                    // already completed the chunk channel, so both tasks are done.
                    await downloadTask;
                    await grouperTask;
                }
                finally
                {
                    // Unblock the grouper if downloads faulted while it was still writing, then observe every
                    // task so nothing faults unobserved (the grouper completes via cancel).
                    await cts.CancelAsync();
                    await Task.WhenAll(grouperTask, downloadTask).ContinueWith(static _ => { }, TaskScheduler.Default);
                }
            }

            // ── Stage 5: Kick off rehydration for archive-tier chunks ─────────────
            // Chunks already pending are not re-requested (StartCopyFromUri 409s on an archived blob that
            // already has a pending copy); re-routed chunks (stale-classification download failures) are.
            var chunksToRequest = classification
                .Where(kv => kv.Value.Status == ChunkHydrationStatus.NeedsRehydration)
                .Select(kv => kv.Key)
                .Concat(rerouteToRehydration.Keys)
                .Distinct()
                .ToList();
            var chunksToRehydrate = chunksToRequest.Count + pendingCount;

            if (chunksToRehydrate > 0)
            {
                long totalRehydrateBytes = 0;
                foreach (var chunkHash in chunksToRequest)
                {
                    try
                    {
                        await chunkStorage.StartRehydrationAsync(chunkHash, rehydratePriority, cancellationToken);
                        if (classification.TryGetValue(chunkHash, out var cc))
                            totalRehydrateBytes += cc.CompressedSize;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to start rehydration for chunk {ChunkHash}", chunkHash);
                    }
                }

                await mediator.Publish(new RehydrationStartedEvent(chunksToRehydrate, totalRehydrateBytes), cancellationToken);
            }

            var totalPending = chunksToRehydrate;

            // ── Stage 6: Cleanup ALL rehydrated blobs in the container ────────────
            if (totalPending == 0)
            {
                await using var cleanupPlan = await chunkStorage.PlanRehydratedCleanupAsync(cancellationToken);
                if (cleanupPlan.ChunkCount > 0)
                {
                    if (opts.ConfirmCleanup is not null && await opts.ConfirmCleanup(cleanupPlan.ChunkCount, cleanupPlan.TotalBytes, cancellationToken))
                    {
                        var cleanupResult = await cleanupPlan.ExecuteAsync(cancellationToken);
                        logger.LogInformation("[cleanup] Deleted {ChunksDeleted} rehydrated blob(s), freed {BytesFreed} bytes", cleanupResult.DeletedChunkCount, cleanupResult.FreedBytes);
                        await mediator.Publish(new CleanupCompleteEvent(cleanupResult.DeletedChunkCount, cleanupResult.FreedBytes), cancellationToken);
                    }
                }
            }

            logger.LogInformation("[restore] Done: restored={Restored} skipped={Skipped} pendingRehydration={Pending}", filesRestored, skipped.Value, totalPending);

            return new RestoreResult
            {
                Success                  = true,
                FilesRestored            = (int)Interlocked.Read(ref filesRestored),
                FilesSkipped             = (int)skipped.Value,
                ChunksPendingRehydration = totalPending,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore pipeline failed");
            return new RestoreResult
            {
                Success                  = false,
                FilesRestored            = 0,
                FilesSkipped             = 0,
                ChunksPendingRehydration = 0,
                ErrorMessage             = ex.Message
            };
        }
    }

    private static ChunkHydrationStatus ClassifyChunk(ChunkHash chunkHash, ShardEntry entry, IReadOnlyDictionary<ChunkHash, bool> rehydratedState)
    {
        // A rehydrated copy (from the single prefix listing) is authoritative: ready → download now,
        // not-ready → still rehydrating. Otherwise the index tier hint decides; archive needs rehydration.
        if (rehydratedState.TryGetValue(chunkHash, out var ready))
            return ready ? ChunkHydrationStatus.Available : ChunkHydrationStatus.RehydrationPending;

        return entry.StorageTierHint == BlobTier.Archive
            ? ChunkHydrationStatus.NeedsRehydration
            : ChunkHydrationStatus.Available;
    }

    // ── Large file restore ───────────────────────────────────────────────────────

    private async Task RestoreLargeFileAsync(
        ChunkHash          chunkHash,
        FileToRestore      file,
        RelativeFileSystem fs,
        RestoreOptions     opts,
        long               compressedSize,
        CancellationToken  cancellationToken)
    {
        {
            var progress = opts.CreateLargeFileDownloadProgress?.Invoke(file.RelativePath, compressedSize);
            await using var payloadStream = await chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
            await using var fileStream   = fs.CreateFile(file.RelativePath);

            await payloadStream.CopyToAsync(fileStream, cancellationToken);
        }

        // Set file timestamps from tree metadata (after the stream is closed).
        fs.SetTimestamps(file.RelativePath, file.Created, file.Modified);

        // Create pointer file.
        if (!opts.NoPointers)
        {
            var pointerPath = file.RelativePath.ToPointerPath();
            await fs.WriteAllTextAsync(pointerPath, file.ContentHash.ToString(), cancellationToken);
            fs.SetTimestamps(pointerPath, file.Created, file.Modified);
        }
    }

    // ── Tar bundle restore ─────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a tar chunk and extracts only the entries whose content hash matches
    /// <paramref name="filesNeeded"/>. Returns the number of files written to disk.
    /// </summary>
    private async Task<int> RestoreTarBundleAsync(
        ChunkHash                                    chunkHash,
        Dictionary<ContentHash, List<FileToRestore>> filesNeeded,
        RelativeFileSystem                           fs,
        RestoreOptions                               opts,
        long                                         compressedSize,
        CancellationToken                            cancellationToken)
    {
        var restored = 0;

        var progress = opts.CreateTarBundleDownloadProgress?.Invoke(chunkHash, compressedSize);
        await using var payloadStream = await chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
        var tarReader = new TarReader(payloadStream, leaveOpen: false);

        while (await tarReader.GetNextEntryAsync(copyData: false, cancellationToken) is { } tarEntry)
        {
            if (!ContentHash.TryParse(tarEntry.Name, out var contentHash))
                throw new FormatException($"Invalid tar entry name '{tarEntry.Name}' in chunk '{chunkHash}'.");

            if (!filesNeeded.TryGetValue(contentHash, out var filesForHash))
                continue; // not needed for this restore — skip

            RelativePath? sourcePath = null;

            for (var i = 0; i < filesForHash.Count; i++)
            {
                var file = filesForHash[i];

                if (tarEntry.DataStream is null)
                {
                    // create an empty file
                    await using var _ = fs.CreateFile(file.RelativePath);
                }
                else if (i == 0)
                {
                    await using var output = fs.CreateFile(file.RelativePath);
                    await tarEntry.DataStream.CopyToAsync(output, cancellationToken);
                    sourcePath = file.RelativePath;
                }
                else
                {
                    // TODO: investigate Async copy? Ref https://github.com/dotnet/runtime/issues/20697, https://github.com/dotnet/runtime/issues/20695
                    fs.CopyFile(sourcePath ?? throw new InvalidOperationException("Tar duplicate copy requires a source path."), file.RelativePath, overwrite: true);
                }

                // Set timestamps.
                fs.SetTimestamps(file.RelativePath, file.Created, file.Modified);

                // Create pointer file.
                if (!opts.NoPointers)
                {
                    var pointerPath = file.RelativePath.ToPointerPath();
                    await fs.WriteAllTextAsync(pointerPath, contentHash.ToString(), cancellationToken);
                    fs.SetTimestamps(pointerPath, file.Created, file.Modified);
                }

                await mediator.Publish(new FileRestoredEvent(file.RelativePath, fs.GetFileSize(file.RelativePath)), cancellationToken);
                restored++;
            }
        }

        // Emit completion event for tar bundles so the CLI can remove the TrackedDownload.
        await mediator.Publish(new ChunkDownloadCompletedEvent(chunkHash, restored, compressedSize), cancellationToken);

        return restored;
    }
}
