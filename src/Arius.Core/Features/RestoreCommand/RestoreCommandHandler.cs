using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
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
/// Restores files from a snapshot to a local directory, as a streaming, memory-bounded Mediator command
/// handler that scales to repositories with millions of file entries. The tree walk is breadth-first and
/// composed as a single <see cref="IAsyncEnumerable{T}"/> — Walk → Route → Resolve — mirroring
/// <c>ListQueryHandler</c>; the only channel is the pass-2 download fan-out.
///
/// A cost estimate and rehydration confirmation must be shown <em>before</em> any download (cancelling
/// must download nothing), so the tree is walked twice. <see cref="IFileTreeService.ReadAsync"/> is
/// cache-backed, so the second walk is cheap.
///
/// ## Stages (numbered to match the <c>// ── Stage N ──</c> banners in <see cref="Handle"/>)
///
/// 1. **Resolve snapshot** — pick the requested (or latest) snapshot; bail if none.
/// 2. **Classify** (walk #1) — Walk → Route → Resolve, accumulating one <see cref="ChunkClassification"/>
///    per distinct chunk (hydration status, summed sizes, refcount). Memory is O(distinct chunks), not
///    O(files). Rehydration state comes from a single rehydrated-prefix listing plus each chunk's index
///    <c>StorageTierHint</c> — no per-chunk blob calls.
/// 3. **Confirm** — publish the cost estimate and invoke <see cref="RestoreOptions.ConfirmRehydration"/>;
///    cancel returns without downloading.
/// 4. **Download** (walk #2, events suppressed) — a grouper dispatches work to download workers: large
///    files (chunk hash == content hash) stream 1:1 immediately; tar bundles buffer per chunk and flush
///    the instant their refcount is met (each tar downloaded exactly once). A blob still archived at
///    download time (a stale classification) is re-routed to rehydration rather than faulting the run.
/// 5. **Rehydrate** — request rehydration for archive-tier chunks (skipping ones already pending).
/// 6. **Cleanup** — when nothing is pending, optionally delete leftover rehydrated blobs.
///
/// ## Pipeline (shared by stages 2 &amp; 4)
///
/// Walk (BFS) ─► Route (conflict check, ×N parallel) ─► Resolve (batched chunk-index lookup), composed
/// as one <see cref="IAsyncEnumerable{T}"/> of <see cref="ResolvedFile"/> via
/// <see cref="WhereParallelAsync"/>. Pass 1 emits per-file/progress events; pass 2 suppresses them.
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

    private const int RouteWorkers     = 8;
    private const int ResolveBatchSize = 32;
    private const int DownloadWorkers  = 4;

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
            var fs = new RelativeFileSystem(LocalDirectory.Parse(opts.RootDirectory));

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
            var rehydratedState = await chunkStorage.ListRehydratedChunksAsync(cancellationToken);
            var skipped         = new StrongBox<long>(0);
            var classification  = new Dictionary<ChunkHash, ChunkClassification>();
            var fileCount       = 0;

            await foreach (var resolved in StreamResolvedFilesAsync(fs, snapshot.RootHash, opts, emitEvents: true, skipped, cancellationToken))
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
            var availableCount = 0;
            var needsCount     = 0;
            var pendingCount   = 0;
            var largeChunks    = 0;
            long totalOriginalBytes   = 0;
            long totalCompressedBytes = 0;
            long downloadBytes        = 0;
            long rehydrationBytes     = 0;
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
                var token = cts.Token;

                // ── Grouper ×1: large files dispatch immediately; tar groups flush at refcount ──
                var grouperTask = Task.Run(async () =>
                {
                    var openTars = new Dictionary<ChunkHash, List<FileToRestore>>();
                    try
                    {
                        await foreach (var resolved in StreamResolvedFilesAsync(fs, snapshot.RootHash, opts, emitEvents: false, skipped: null, token))
                        {
                            var chunkHash = resolved.IndexEntry.ChunkHash;
                            if (!classification.TryGetValue(chunkHash, out var cc) || cc.Status != ChunkHydrationStatus.Available)
                                continue; // not downloadable now (rehydration handled by Stage 5)

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

    // ── Pipeline: Walk → Route → Resolve, composed as one stream ─────────────────

    /// <summary>
    /// Streams the files to restore as Walk → Route → Resolve composed into one <see cref="IAsyncEnumerable{T}"/>.
    /// <paramref name="emitEvents"/> is <c>true</c> only for the classify pass (avoids double-publishing
    /// per-file / progress events on the download re-walk).
    /// </summary>
    private IAsyncEnumerable<ResolvedFile> StreamResolvedFilesAsync(
        RelativeFileSystem fs,
        FileTreeHash       rootHash,
        RestoreOptions     opts,
        bool               emitEvents,
        StrongBox<long>?   skipped,
        CancellationToken  cancellationToken)
        => ResolveAsync(
               WalkAsync(rootHash, opts.TargetPath, emitProgress: emitEvents, cancellationToken)
                   .WhereParallelAsync(
                       RouteWorkers,
                       (file, ct) => ShouldRestoreAsync(file, fs, opts, emitEvents, skipped, ct),
                       cancellationToken),
               cancellationToken);

    // ── Stage A: Walk (breadth-first, mirrors ListQueryHandler) ──────────────────

    /// <summary>
    /// Breadth-first walk of the snapshot tree, yielding one <see cref="FileToRestore"/> per file that
    /// matches <paramref name="targetPrefix"/> (or all files when <c>null</c>). Memory is bounded by the
    /// directory width plus the traversal frontier, not by the file count.
    /// </summary>
    private async IAsyncEnumerable<FileToRestore> WalkAsync(
        FileTreeHash  rootHash,
        RelativePath? targetPrefix,
        bool          emitProgress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var total                 = 0;
        var emittedSinceLastEvent = 0;
        var lastEmit              = DateTimeOffset.UtcNow;

        var pending = new Queue<(FileTreeHash TreeHash, RelativePath Path)>();
        pending.Enqueue((rootHash, RelativePath.Root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (treeHash, currentPath) = pending.Dequeue();

            // Skip entire subtrees that cannot match the prefix filter.
            if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix.Value))
                continue;

            var entries = await fileTreeService.ReadAsync(treeHash, cancellationToken);

            // Files first…
            foreach (var fileEntry in entries.OfType<FileEntry>())
            {
                var entryPath = currentPath / fileEntry.Name;
                if (targetPrefix is not null && !entryPath.StartsWith(targetPrefix.Value))
                    continue;

                yield return new FileToRestore(entryPath, fileEntry.ContentHash, fileEntry.Created, fileEntry.Modified);
                total++;
                emittedSinceLastEvent++;

                if (emitProgress)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (emittedSinceLastEvent >= 10 || (now - lastEmit).TotalMilliseconds >= 100)
                    {
                        emittedSinceLastEvent = 0;
                        lastEmit = now;
                        logger.LogDebug("[tree] Traversal progress: {FilesFound} files discovered", total);
                        await mediator.Publish(new TreeTraversalProgressEvent(total), cancellationToken);
                    }
                }
            }

            // …then enqueue subdirectories (the queue is the breadth-first worklist).
            foreach (var directoryEntry in entries.OfType<DirectoryEntry>())
                pending.Enqueue((directoryEntry.FileTreeHash, currentPath / directoryEntry.Name));
        }

        if (emitProgress && total > 0)
            await mediator.Publish(new TreeTraversalProgressEvent(total), cancellationToken);
    }

    private static bool IsPathRelevant(RelativePath currentPath, RelativePath targetPrefix)
    {
        return currentPath == RelativePath.Root
            || targetPrefix.StartsWith(currentPath)
            || currentPath.StartsWith(targetPrefix);
    }

    // ── Stage B: Route (conflict check) ──────────────────────────────────────────

    /// <summary>
    /// Decides one file's fate against the local filesystem and returns whether it should be restored:
    /// skip identical files, keep locally-differing files unless <c>--overwrite</c>, otherwise restore.
    /// Per-file events are emitted only when <paramref name="emitEvents"/> is set (classify pass).
    /// Runs on multiple route workers, so <paramref name="skipped"/> is updated with <see cref="Interlocked"/>.
    /// </summary>
    private async ValueTask<bool> ShouldRestoreAsync(
        FileToRestore      file,
        RelativeFileSystem fs,
        RestoreOptions     opts,
        bool               emitEvents,
        StrongBox<long>?   skipped,
        CancellationToken  cancellationToken)
    {
        if (fs.FileExists(file.RelativePath))
        {
            if (!opts.Overwrite)
            {
                // Hash the local file to check whether it already matches.
                await using var s = fs.OpenRead(file.RelativePath);
                var localHash = await encryption.ComputeHashAsync(s, cancellationToken);

                if (localHash == file.ContentHash)
                {
                    if (skipped is not null) Interlocked.Increment(ref skipped.Value);
                    if (emitEvents)
                    {
                        logger.LogInformation("[route] {Path} -> skip (identical)", file.RelativePath);
                        await mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length), cancellationToken);
                        await mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.SkipIdentical, s.Length), cancellationToken);
                    }
                    return false;
                }

                // Exists with a different hash and no --overwrite → keep the local copy.
                if (skipped is not null) Interlocked.Increment(ref skipped.Value);
                if (emitEvents)
                {
                    logger.LogInformation("[route] {Path} -> keep (local differs, no --overwrite)", file.RelativePath);
                    await mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length), cancellationToken);
                    await mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.KeepLocalDiffers, s.Length), cancellationToken);
                }
                return false;
            }

            if (emitEvents)
            {
                logger.LogInformation("[route] {Path} -> overwrite", file.RelativePath);
                await mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.Overwrite, fs.GetFileSize(file.RelativePath)), cancellationToken);
            }
        }
        else if (emitEvents)
        {
            logger.LogInformation("[route] {Path} -> new", file.RelativePath);
            await mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.New, 0), cancellationToken);
        }

        return true;
    }

    // ── Stage C: Resolve (batched chunk-index lookup) ────────────────────────────

    /// <summary>
    /// Buffers up to <see cref="ResolveBatchSize"/> files into one chunk-index lookup, yielding each file
    /// paired with its index entry. Throws if the snapshot references a content hash missing from the index.
    /// </summary>
    private async IAsyncEnumerable<ResolvedFile> ResolveAsync(
        IAsyncEnumerable<FileToRestore> files,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<FileToRestore>(ResolveBatchSize);

        await foreach (var file in files.WithCancellation(cancellationToken))
        {
            batch.Add(file);
            if (batch.Count >= ResolveBatchSize)
            {
                await foreach (var resolved in ResolveBatchAsync(batch, cancellationToken))
                    yield return resolved;
                batch.Clear();
            }
        }

        await foreach (var resolved in ResolveBatchAsync(batch, cancellationToken))
            yield return resolved;
    }

    private async IAsyncEnumerable<ResolvedFile> ResolveBatchAsync(
        List<FileToRestore> batch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            yield break;

        var entries = await index.LookupAsync(batch.Select(f => f.ContentHash).Distinct(), cancellationToken);

        List<FileToRestore>? missing = null;
        foreach (var file in batch)
        {
            if (!entries.TryGetValue(file.ContentHash, out var entry))
            {
                (missing ??= []).Add(file);
                continue;
            }

            yield return new ResolvedFile(file, entry);
        }

        if (missing is not null)
        {
            var sample = string.Join(", ", missing.Take(5).Select(f => $"{f.RelativePath} ({f.ContentHash.Short8})"));
            throw new InvalidOperationException($"Snapshot references {missing.Count} content hash(es) that are missing from the chunk index: {sample}. Run the explicit chunk-index repair command and retry restore.");
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
