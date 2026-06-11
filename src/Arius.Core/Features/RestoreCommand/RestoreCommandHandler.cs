using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;
using ChunkHydrationStatus = Arius.Core.Shared.ChunkStorage.ChunkHydrationStatus;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Implements the restore pipeline as a streaming, channel-based Mediator command handler, with bounded
/// memory that scales to repositories with millions of file entries. It mirrors the staged-channel
/// structure of <c>ArchiveCommandHandler</c> and the streaming walk + batched resolve of
/// <c>ListQueryHandler</c>.
///
/// Because a cost estimate + rehydration confirmation must be presented <em>before</em> any download
/// (and cancelling must download nothing), the pipeline runs in two streaming passes over the tree
/// (<see cref="IFileTreeService.ReadAsync"/> is cache-backed, so re-walking is cheap):
///
/// **Pass 1 — Classify.** Walk → Route → Resolve, then a single consumer builds a per-distinct-chunk
/// <see cref="ChunkClassification"/> map (status, summed sizes, refcount). Memory is O(distinct chunks),
/// not O(files). Rehydration state comes from one <see cref="IChunkStorageService.ListRehydratedChunksAsync"/>
/// listing + each chunk's index <c>StorageTierHint</c> — no per-chunk blob calls. The cost estimate is
/// then published and <see cref="RestoreOptions.ConfirmRehydration"/> is invoked; cancel returns without
/// downloading.
///
/// **Pass 2 — Download.** Walk → Route → Resolve again (events suppressed), then a grouper dispatches
/// work to download workers: large files (chunk hash == content hash) stream 1:1 immediately; tar bundles
/// are buffered per chunk and flushed the instant their pass-1 refcount is met (so each tar is downloaded
/// exactly once). A download that still hits an archived blob (a stale classification) is caught and the
/// chunk is re-routed to the rehydration set.
///
/// ## Stages (per pass)
///
/// 1. **Walk** (×1) — explicit-stack depth-first walk of the snapshot tree; honours <c>TargetPath</c>.
/// 2. **Route** (×N) — conflict check: skip identical local files, honour <c>--overwrite</c>; emits files
///    that need restoring. Per-file events are emitted in pass 1 only.
/// 3. **Resolve** (×1) — batches ≤ <see cref="ResolveBatchSize"/> files into one chunk-index lookup.
///
/// ## Channels (per pass)
///
/// | Channel           | Writer        | Reader              | Capacity            | Notes                                          |
/// |-------------------|---------------|---------------------|---------------------|------------------------------------------------|
/// | `walkChannel`     | Walk (1)      | Route (×N)          | bounded (N)         | Backpressure caps how far the walk runs ahead. |
/// | `routeChannel`    | Route (×N)    | Resolve (1)         | bounded (N)         | Files that need restoring.                     |
/// | `resolvedChannel` | Resolve (1)   | classify/grouper (1)| bounded (N)         | `ResolvedFile` (file + its index entry).       |
/// | `chunkChannel`    | Grouper (1)   | Download (×N)       | bounded (workers)   | Pass 2 only; download backpressures grouping.  |
/// </summary>
public sealed class RestoreCommandHandler
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    // ── Concurrency knobs ──────────────────────────────────────────────────────

    private const int ChannelCapacity  = 256;
    private const int RouteWorkers     = 8;
    private const int ResolveBatchSize = 32;
    private const int DownloadWorkers  = 4;

    // ── Dependencies ───────────────────────────────────────────────────────────

    private readonly IEncryptionService             _encryption;
    private readonly IChunkIndexService             _index;
    private readonly IChunkStorageService           _chunkStorage;
    private readonly IFileTreeService               _fileTreeService;
    private readonly ISnapshotService               _snapshotSvc;
    private readonly IMediator                      _mediator;
    private readonly ILogger<RestoreCommandHandler> _logger;
    private readonly string                         _accountName;
    private readonly string                         _containerName;

    public RestoreCommandHandler(
        IEncryptionService             encryption,
        IChunkIndexService             index,
        IChunkStorageService           chunkStorage,
        IFileTreeService               fileTreeService,
        ISnapshotService               snapshotSvc,
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
    /// Executes the end-to-end restore pipeline for the provided <see cref="RestoreCommand"/>. See the
    /// type-level documentation for the two-pass stage/channel breakdown.
    /// </summary>
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // ── Operation start marker ────────────────────────────────────────────
        _logger.LogInformation("[restore] Start: target={RootDir} account={Account} container={Container} version={Version} overwrite={Overwrite}", opts.RootDirectory, _accountName, _containerName, opts.Version ?? "latest", opts.Overwrite);

        try
        {
            var fs = new RelativeFileSystem(LocalDirectory.Parse(opts.RootDirectory));

            // ── Step 1: Resolve snapshot ──────────────────────────────────────
            _logger.LogInformation("[phase] resolve-snapshot");
            var snapshot = await _snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
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

            _logger.LogInformation("[snapshot] Resolved: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

            // ── Pass 1: Classify (streaming, bounded) ─────────────────────────
            _logger.LogInformation("[phase] classify");
            var skipped        = new StrongBox<long>(0);
            var classification = new Dictionary<ChunkHash, ChunkClassification>();
            var fileCount      = await ClassifyAsync(fs, snapshot.RootHash, opts, classification, skipped, cancellationToken);

            _logger.LogInformation("[tree] Traversal complete: {Count} file(s) collected", fileCount);

            // Counts + byte sums from the (bounded) classification map.
            var availableCount = 0;
            var needsCount     = 0;
            var pendingCount   = 0;
            var largeChunks    = 0;
            long totalOriginalBytes   = 0;
            long totalCompressedBytes = 0;
            long downloadBytes        = 0;
            long rehydrationBytes      = 0;
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

            await _mediator.Publish(new SnapshotResolvedEvent(snapshot.Timestamp, snapshot.RootHash, fileCount), cancellationToken);
            await _mediator.Publish(new TreeTraversalCompleteEvent(fileCount, totalOriginalBytes), cancellationToken);
            await _mediator.Publish(new RestoreStartedEvent(fileCount), cancellationToken);

            _logger.LogInformation("[chunk] Resolution: {Groups} chunk group(s), large={Large}, tar={Tar}", classification.Count, largeChunks, tarChunks);
            await _mediator.Publish(new ChunkResolutionCompleteEvent(classification.Count, largeChunks, tarChunks, totalOriginalBytes, totalCompressedBytes), cancellationToken);

            _logger.LogInformation("[rehydration] Status: available={Available} rehydrated={Rehydrated} needsRehydration={NeedsRehydration} pending={Pending}", availableCount, 0, needsCount, pendingCount);
            await _mediator.Publish(new RehydrationStatusEvent(availableCount, 0, needsCount, pendingCount), cancellationToken);

            // ── Step 6: Cost estimation and confirmation ──────────────────────
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

            // ── Pass 2: Download available chunks (streaming, bounded) ────────
            var rerouteToRehydration = new ConcurrentDictionary<ChunkHash, bool>();
            var filesRestored        = 0;

            if (availableCount > 0)
            {
                _logger.LogInformation("[phase] download");
                filesRestored = await DownloadAsync(fs, snapshot.RootHash, opts, classification, rerouteToRehydration, cancellationToken);
            }

            // ── Step 8: Kick off rehydration for archive-tier chunks ──────────
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
                        await _chunkStorage.StartRehydrationAsync(chunkHash, rehydratePriority, cancellationToken);
                        if (classification.TryGetValue(chunkHash, out var cc))
                            totalRehydrateBytes += cc.CompressedSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to start rehydration for chunk {ChunkHash}", chunkHash);
                    }
                }

                await _mediator.Publish(new RehydrationStartedEvent(chunksToRehydrate, totalRehydrateBytes), cancellationToken);
            }

            var totalPending = chunksToRehydrate;

            // ── Step 9: Cleanup ALL rehydrated blobs in the container ─────────
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

            _logger.LogInformation("[restore] Done: restored={Restored} skipped={Skipped} pendingRehydration={Pending}", filesRestored, skipped.Value, totalPending);

            return new RestoreResult
            {
                Success                  = true,
                FilesRestored            = filesRestored,
                FilesSkipped             = (int)skipped.Value,
                ChunksPendingRehydration = totalPending,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore pipeline failed");
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

    // ── Pass 1: Classify ───────────────────────────────────────────────────────

    /// <summary>
    /// Streams Walk → Route → Resolve and accumulates the per-distinct-chunk <see cref="ChunkClassification"/>
    /// map (status, summed sizes, refcount). Returns the number of files that need restoring.
    /// </summary>
    private async Task<int> ClassifyAsync(
        RelativeFileSystem                       fs,
        FileTreeHash                             rootHash,
        RestoreOptions                           opts,
        Dictionary<ChunkHash, ChunkClassification> classification,
        StrongBox<long>                          skipped,
        CancellationToken                        cancellationToken)
    {
        var rehydratedState = await _chunkStorage.ListRehydratedChunksAsync(cancellationToken);

        var fileCount = 0;
        var pipeline  = StartResolvePipeline(fs, rootHash, opts, emitEvents: true, skipped, cancellationToken);
        using (pipeline.Cts)
        {
            try
            {
                await foreach (var resolved in pipeline.Reader.ReadAllAsync(cancellationToken))
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
            }
            finally
            {
                await pipeline.Cts.CancelAsync();
                await pipeline.Stages;
            }
        }

        return fileCount;
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

    // ── Pass 2: Download ─────────────────────────────────────────────────────────

    /// <summary>
    /// Re-streams Walk → Route → Resolve (events suppressed) then groups + downloads available chunks.
    /// Returns the number of files restored.
    /// </summary>
    private async Task<int> DownloadAsync(
        RelativeFileSystem                         fs,
        FileTreeHash                               rootHash,
        RestoreOptions                             opts,
        IReadOnlyDictionary<ChunkHash, ChunkClassification> classification,
        ConcurrentDictionary<ChunkHash, bool>      rerouteToRehydration,
        CancellationToken                          cancellationToken)
    {
        long filesRestored = 0;

        var chunkChannel = Channel.CreateBounded<ChunkToRestore>(new BoundedChannelOptions(DownloadWorkers) { SingleWriter = true, SingleReader = false });
        opts.OnDownloadQueueReady?.Invoke(() => chunkChannel.Reader.Count);

        var pipeline = StartResolvePipeline(fs, rootHash, opts, emitEvents: false, skipped: null, cancellationToken);
        using (pipeline.Cts)
        {
            // All pass-2 work shares the pipeline's linked token so a download fault (below) can cancel
            // the grouper — otherwise it would block forever writing to the bounded chunk channel that
            // the faulted workers stopped draining.
            var token = pipeline.Cts.Token;

            // ── Grouper ×1: large files dispatch immediately; tar groups flush at refcount ──
            var grouperTask = Task.Run(async () =>
            {
                var openTars = new Dictionary<ChunkHash, List<FileToRestore>>();
                try
                {
                    await foreach (var resolved in pipeline.Reader.ReadAllAsync(token))
                    {
                        var chunkHash = resolved.IndexEntry.ChunkHash;
                        if (!classification.TryGetValue(chunkHash, out var cc) || cc.Status != ChunkHydrationStatus.Available)
                            continue; // not downloadable now (rehydration handled by the caller)

                        if (cc.IsLargeChunk)
                        {
                            await chunkChannel.Writer.WriteAsync(
                                new ChunkToRestore(chunkHash, IsLargeChunk: true, new[] { resolved.File }, cc.CompressedSize, cc.OriginalSize),
                                token);
                            continue;
                        }

                        if (!openTars.TryGetValue(chunkHash, out var list))
                            openTars[chunkHash] = list = [];
                        list.Add(resolved.File);

                        // All of this tar's to-restore files have arrived → download it exactly once.
                        if (list.Count >= cc.RefCount)
                        {
                            openTars.Remove(chunkHash);
                            await chunkChannel.Writer.WriteAsync(
                                new ChunkToRestore(chunkHash, IsLargeChunk: false, list, cc.CompressedSize, cc.OriginalSize),
                                token);
                        }
                    }

                    // Defensive: flush any tar groups that never reached their refcount.
                    foreach (var (chunkHash, list) in openTars)
                    {
                        var cc = classification[chunkHash];
                        await chunkChannel.Writer.WriteAsync(
                            new ChunkToRestore(chunkHash, IsLargeChunk: false, list, cc.CompressedSize, cc.OriginalSize),
                            token);
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
                chunkChannel.Reader.ReadAllAsync(token),
                new ParallelOptions { MaxDegreeOfParallelism = DownloadWorkers, CancellationToken = token },
                async (chunk, ct) =>
                {
                    _logger.LogInformation("[download] Chunk {ChunkHash} ({Type}, {FileCount} file(s), compressed={Compressed})", chunk.ChunkHash.Short8, chunk.IsLargeChunk ? "large" : "tar", chunk.Files.Count, chunk.CompressedSize.Bytes().Humanize());

                    // Publish the start event before invoking the tar progress factory: the CLI's
                    // ChunkDownloadStarted handler populates the metadata that CreateTarBundleDownloadProgress reads.
                    await _mediator.Publish(new ChunkDownloadStartedEvent(chunk.ChunkHash, chunk.IsLargeChunk ? "large" : "tar", chunk.Files.Count, chunk.CompressedSize, chunk.OriginalSize), ct);

                    try
                    {
                        if (chunk.IsLargeChunk)
                        {
                            foreach (var file in chunk.Files)
                            {
                                await RestoreLargeFileAsync(chunk.ChunkHash, file, fs, opts, chunk.CompressedSize, ct);
                                Interlocked.Increment(ref filesRestored);
                                await _mediator.Publish(new FileRestoredEvent(file.RelativePath, chunk.OriginalSize), ct);
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
                        _logger.LogWarning("[download] Chunk {ChunkHash} is archived despite classification; re-routing to rehydration", chunk.ChunkHash.Short8);
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
                // task so nothing faults unobserved (stages catch internally; grouper completes via cancel).
                await pipeline.Cts.CancelAsync();
                await Task.WhenAll(grouperTask, downloadTask).ContinueWith(static _ => { }, TaskScheduler.Default);
                await pipeline.Stages;
            }
        }

        return (int)Interlocked.Read(ref filesRestored);
    }

    // ── Shared Walk → Route → Resolve stages ─────────────────────────────────────

    private readonly record struct ResolvePipeline(ChannelReader<ResolvedFile> Reader, Task Stages, CancellationTokenSource Cts);

    /// <summary>
    /// Starts the three shared streaming stages (Walk → Route → Resolve) and returns the resolved-file
    /// reader. Each stage completes its output writer in a <c>finally</c>; faults propagate via
    /// <c>Writer.Complete(exception)</c> so the consumer rethrows them. <paramref name="emitEvents"/>
    /// is <c>true</c> only for pass 1 (avoids double-publishing per-file/progress events).
    /// </summary>
    private ResolvePipeline StartResolvePipeline(
        RelativeFileSystem fs,
        FileTreeHash       rootHash,
        RestoreOptions     opts,
        bool               emitEvents,
        StrongBox<long>?   skipped,
        CancellationToken  cancellationToken)
    {
        var walkChannel     = Channel.CreateBounded<FileToRestore>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter = true,  SingleReader = false });
        var routeChannel    = Channel.CreateBounded<FileToRestore>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter = false, SingleReader = true  });
        var resolvedChannel = Channel.CreateBounded<ResolvedFile>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter = true,  SingleReader = true  });

        var stageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var walkTask = Task.Run(async () =>
        {
            try
            {
                await WalkAsync(walkChannel.Writer, rootHash, opts.TargetPath, emitEvents, stageCts.Token);
                walkChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                walkChannel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        var routeTask = Task.Run(async () =>
        {
            try
            {
                await RouteAsync(walkChannel.Reader, routeChannel.Writer, fs, opts, emitEvents, skipped, stageCts.Token);
                routeChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                routeChannel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        var resolveTask = Task.Run(async () =>
        {
            try
            {
                await ResolveAsync(routeChannel.Reader, resolvedChannel.Writer, stageCts.Token);
                resolvedChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                resolvedChannel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        return new ResolvePipeline(resolvedChannel.Reader, Task.WhenAll(walkTask, routeTask, resolveTask), stageCts);
    }

    // ── Stage 1: Walk ────────────────────────────────────────────────────────────

    /// <summary>
    /// Explicit-stack depth-first walk of the snapshot tree, emitting one <see cref="FileToRestore"/> per
    /// file that matches <paramref name="targetPrefix"/> (or all files when <c>null</c>). O(1) per entry.
    /// </summary>
    private async Task WalkAsync(
        ChannelWriter<FileToRestore> writer,
        FileTreeHash                 rootHash,
        RelativePath?                targetPrefix,
        bool                         emitProgress,
        CancellationToken            cancellationToken)
    {
        var total                 = 0;
        var emittedSinceLastEvent = 0;
        var lastEmit              = DateTimeOffset.UtcNow;

        var pending = new Stack<(FileTreeHash TreeHash, RelativePath Path)>();
        pending.Push((rootHash, RelativePath.Root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (treeHash, currentPath) = pending.Pop();

            // Skip entire subtrees that cannot match the prefix filter.
            if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix.Value))
                continue;

            var entries     = await _fileTreeService.ReadAsync(treeHash, cancellationToken);
            var childDirectories = new List<(FileTreeHash, RelativePath)>();

            foreach (var entry in entries)
            {
                if (entry is DirectoryEntry directoryEntry)
                {
                    childDirectories.Add((directoryEntry.FileTreeHash, currentPath / directoryEntry.Name));
                }
                else if (entry is FileEntry fileEntry)
                {
                    var entryPath = currentPath / fileEntry.Name;
                    if (targetPrefix is not null && !entryPath.StartsWith(targetPrefix.Value))
                        continue;

                    await writer.WriteAsync(
                        new FileToRestore(entryPath, fileEntry.ContentHash, fileEntry.Created, fileEntry.Modified),
                        cancellationToken);
                    total++;
                    emittedSinceLastEvent++;

                    if (emitProgress)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (emittedSinceLastEvent >= 10 || (now - lastEmit).TotalMilliseconds >= 100)
                        {
                            emittedSinceLastEvent = 0;
                            lastEmit = now;
                            _logger.LogDebug("[tree] Traversal progress: {FilesFound} files discovered", total);
                            await _mediator.Publish(new TreeTraversalProgressEvent(total), cancellationToken);
                        }
                    }
                }
            }

            // Push in reverse so the stack pops children in listing (pre-order) order.
            for (var i = childDirectories.Count - 1; i >= 0; i--)
                pending.Push(childDirectories[i]);
        }

        if (emitProgress && total > 0)
            await _mediator.Publish(new TreeTraversalProgressEvent(total), cancellationToken);
    }

    private static bool IsPathRelevant(RelativePath currentPath, RelativePath targetPrefix)
    {
        return currentPath == RelativePath.Root
            || targetPrefix.StartsWith(currentPath)
            || currentPath.StartsWith(targetPrefix);
    }

    // ── Stage 2: Route (conflict check) ──────────────────────────────────────────

    /// <summary>
    /// Decides each file's fate against the local filesystem: skip identical files, keep locally-differing
    /// files unless <c>--overwrite</c>, otherwise forward for restoring. Per-file events are emitted only
    /// when <paramref name="emitEvents"/> is set (pass 1).
    /// </summary>
    private async Task RouteAsync(
        ChannelReader<FileToRestore> reader,
        ChannelWriter<FileToRestore> writer,
        RelativeFileSystem           fs,
        RestoreOptions               opts,
        bool                         emitEvents,
        StrongBox<long>?             skipped,
        CancellationToken            cancellationToken)
    {
        await Parallel.ForEachAsync(
            reader.ReadAllAsync(cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = RouteWorkers, CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                if (fs.FileExists(file.RelativePath))
                {
                    if (!opts.Overwrite)
                    {
                        // Hash the local file to check whether it already matches.
                        await using var s = fs.OpenRead(file.RelativePath);
                        var localHash = await _encryption.ComputeHashAsync(s, ct);

                        if (localHash == file.ContentHash)
                        {
                            if (skipped is not null) Interlocked.Increment(ref skipped.Value);
                            if (emitEvents)
                            {
                                _logger.LogInformation("[route] {Path} -> skip (identical)", file.RelativePath);
                                await _mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length), ct);
                                await _mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.SkipIdentical, s.Length), ct);
                            }
                            return;
                        }

                        // Exists with a different hash and no --overwrite → keep the local copy.
                        if (skipped is not null) Interlocked.Increment(ref skipped.Value);
                        if (emitEvents)
                        {
                            _logger.LogInformation("[route] {Path} -> keep (local differs, no --overwrite)", file.RelativePath);
                            await _mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length), ct);
                            await _mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.KeepLocalDiffers, s.Length), ct);
                        }
                        return;
                    }

                    if (emitEvents)
                    {
                        _logger.LogInformation("[route] {Path} -> overwrite", file.RelativePath);
                        await _mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.Overwrite, fs.GetFileSize(file.RelativePath)), ct);
                    }
                }
                else if (emitEvents)
                {
                    _logger.LogInformation("[route] {Path} -> new", file.RelativePath);
                    await _mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.New, 0), ct);
                }

                await writer.WriteAsync(file, ct);
            });
    }

    // ── Stage 3: Resolve (batched chunk-index lookup) ────────────────────────────

    private async Task ResolveAsync(
        ChannelReader<FileToRestore> reader,
        ChannelWriter<ResolvedFile>  writer,
        CancellationToken            cancellationToken)
    {
        var batch = new List<FileToRestore>(ResolveBatchSize);

        async Task FlushAsync()
        {
            if (batch.Count == 0)
                return;

            var entries = await _index.LookupAsync(batch.Select(f => f.ContentHash).Distinct(), cancellationToken);

            List<FileToRestore>? missing = null;
            foreach (var file in batch)
            {
                if (!entries.TryGetValue(file.ContentHash, out var entry))
                {
                    (missing ??= []).Add(file);
                    continue;
                }

                await writer.WriteAsync(new ResolvedFile(file, entry), cancellationToken);
            }

            if (missing is not null)
            {
                var sample = string.Join(", ", missing.Take(5).Select(f => $"{f.RelativePath} ({f.ContentHash.Short8})"));
                throw new InvalidOperationException($"Snapshot references {missing.Count} content hash(es) that are missing from the chunk index: {sample}. Run the explicit chunk-index repair command and retry restore.");
            }

            batch.Clear();
        }

        await foreach (var file in reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(file);
            if (batch.Count >= ResolveBatchSize)
                await FlushAsync();
        }

        await FlushAsync();
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
            await using var payloadStream = await _chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
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
    /// Downloads a tar bundle and extracts only the entries whose content-hash matches
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
        await using var payloadStream = await _chunkStorage.DownloadAsync(chunkHash, progress, cancellationToken);
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

                await _mediator.Publish(new FileRestoredEvent(file.RelativePath, fs.GetFileSize(file.RelativePath)), cancellationToken);
                restored++;
            }
        }

        // Emit completion event for tar bundles so the CLI can remove the TrackedDownload.
        await _mediator.Publish(new ChunkDownloadCompletedEvent(chunkHash, restored, compressedSize), cancellationToken);

        return restored;
    }
}
