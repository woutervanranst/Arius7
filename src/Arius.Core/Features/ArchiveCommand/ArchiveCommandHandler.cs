using System.Collections.Concurrent;
using System.Threading.Channels;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Extensions;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Streaming;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ArchiveCommand;

/// <summary>
/// Implements the full archive pipeline as a Mediator command handler.
///
/// The pipeline is a set of long-running stages connected by <see cref="System.Threading.Channels.Channel{T}"/>s.
/// Each stage drains its input channel(s), does its work, and writes to its output channel(s); a stage
/// completes its output writer in a <c>finally</c> so downstream stages drain to completion in turn.
///
/// ## Stages
///
/// 1. **Enumerate** (×1) — walk the source tree, publish a `FileScannedEvent` per file.
/// 2. **Hash** (×N) — re-hash binaries (or reuse the pointer hash); skip unreadable files.
/// 3. **Dedup + Router** (×1) — look up the chunk index + the in-run `inFlightHashes` set:
///    - **hit** → dedup (emit only a filetree update; no upload, no index entry)
///    - **new** → route by size vs `opts.SmallFileThreshold` → large or small upload
/// 4. **Upload** (runs concurrently):
///    - **4a. Large Upload** (×N) — one chunk per file via `UploadLargeAsync`.
///    - **4b. Tar Builder** (×1) — pack small files into bundles of `opts.TarTargetSize`.
///    - **4c. Tar Upload** (×N) — upload the bundle, then fan out to thin-chunk uploads (xN), one per entry.
/// 5. **Local-state consumers** (drain what 3/4a/4c emit):
///    - **5a. Chunk-index** (×1) — batch 256 entries into single-writer SQLite transactions.
///    - **5b. Filetree** (×N) — append staging entries (stripe-locked writer) and
///      collect `pendingPointers` / `pendingDeletes`.
/// 6. **End-of-pipeline** (mostly sequential, after all stages above drain):
///    - **6a. Validate filetrees** (×1) — `ValidateAsync`; invalidate index caches on snapshot mismatch.
///    - **6b. Flush chunk index** — `_chunkIndex.FlushAsync`; runs concurrently with 6c (`Task.WhenAll`).
///    - **6c. Build file tree** — `FileTreeBuilder.SynchronizeAsync`; runs concurrently with 6b and yields the snapshot root hash.
///    - **6d. Create snapshot** (×1) — create + promote a snapshot for the root hash (skipped if unchanged).
///    - **6e. Write pointers** (×N) — write `pendingPointers` in parallel (unless `--no-pointers`); runs concurrently with 6f (`Task.WhenAll`).
///    - **6f. Remove local** (×N) — delete `pendingDeletes` in parallel (only if `--remove-local`); runs concurrently with 6e. Disjoint paths from 6e (pointer sidecar vs binary).
///
/// ```
/// Enumerate ─► Hash ─► Dedup+Router ─┬─► Large Upload ───────────────┐
///                                    └─► Tar Builder ─► Tar Upload ──┤
///                                                                    ├─► Chunk-index consumer ─┐                             ┌─► flush chunk index (6b) ─┐                         ┌─► write pointers (6e) ───────┐
///                                                                    └─► Filetree consumer ────┴──► validate filetrees (6a) ─┤                           ├─► create snapshot (6d) ─┤                              ├─► done
///                                                                                                                            └─► build file tree (6c) ───┘                         └─► remove local binaries (6f) ┘
/// ```
///
/// ## Channels
///
/// | Channel                  | Writer                       | Reader                     | Capacity        | Notes                                                       |
/// |--------------------------|------------------------------|----------------------------|-----------------|-------------------------------------------------------------|
/// | `filePairChannel`        | Enumerate (1)                | Hash (2)                   | bounded (N)     | Backpressure caps how far enumeration runs ahead of hashing.|
/// | `hashedChannel`          | Hash (2)                     | Dedup + Router (3)         | unbounded       | Metadata only (path+hash); lets hashing run ahead of upload.|
/// | `largeChannel`           | Dedup + Router (3)           | Large Upload (4a)          | unbounded       | Large-file route (≥ `SmallFileThreshold`).                  |
/// | `smallChannel`           | Dedup + Router (3)           | Tar Builder (4b)           | unbounded       | Small-file route (&lt; `SmallFileThreshold`).               |
/// | `sealedTarChannel`       | Tar Builder (4b)             | Tar Upload (4c)            | bounded (1)     | Carries actual tar bytes, so it stays bounded.              |
/// | `chunkIndexEntryChannel` | Large/Tar Upload (4a/4c)     | Chunk-index consumer (5a)  | unbounded       | `ShardEntry` records.                                       |
/// | `fileTreeEntryChannel`   | Dedup + Upload (3/4a/4c)     | Filetree consumer (5b)     | unbounded       | `HashedFilePair` records (timestamps captured at hash time).|
/// | `pendingPointers`        | Filetree consumer (5b)       | Write pointers (6e)        | `ConcurrentBag` | Not a channel; per-file pointer-write intents.              |
/// | `pendingDeletes`         | Filetree consumer (5b)       | Remove local binaries (6f) | `ConcurrentBag` | Not a channel; per-file local-delete intents.               |
///
/// ## Events
///
/// | Event                    | Emitted by                       |
/// |--------------------------|----------------------------------|
/// | `FileScannedEvent`       | Enumerate (1)                    |
/// | `ScanCompleteEvent`      | Enumerate (1)                    |
/// | `FileHashingEvent`       | Hash (2)                         |
/// | `FileHashedEvent`        | Hash (2)                         |
/// | `FileSkippedEvent`       | Hash (2), Large Upload (4a), Tar Builder (4b) |
/// | `ChunkUploadingEvent`    | Large Upload (4a), Tar Upload (4c) |
/// | `ChunkUploadedEvent`     | Large Upload (4a)                |
/// | `TarBundleStartedEvent`  | Tar Builder (4b)                 |
/// | `TarEntryAddedEvent`     | Tar Builder (4b)                 |
/// | `TarBundleSealingEvent`  | Tar Builder (4b)                 |
/// | `TarBundleUploadedEvent` | Tar Upload (4c)                  |
/// | `SnapshotCreatedEvent`   | Create snapshot (6d)             |
/// </summary>
public sealed class ArchiveCommandHandler : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    // ── Concurrency knobs ─────────────────────────────────────────────────────

    private const int HashWorkers      = 4;
    private const int UploadWorkers    = 4;
    private const int TarUploadWorkers = 2;
    private const int ThinEntryWorkers = 64;
    private const int FileTreeUpdateWorkers = 16;
    private const int ChannelCapacity  = 64;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobContainerService          _blobs;
    private readonly IEncryptionService             _encryption;
    private readonly IChunkIndexService             _chunkIndex;
    private readonly IChunkStorageService           _chunkStorage;
    private readonly IFileTreeService              _fileTreeService;
    private readonly ISnapshotService               _snapshotSvc;
    private readonly IMediator                      _mediator;
    private readonly ILogger<ArchiveCommandHandler> _logger;
    private readonly ILoggerFactory                 _loggerFactory;
    private readonly string                         _accountName;
    private readonly string                         _containerName;
    private readonly Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>> _openStagingSession;

    public ArchiveCommandHandler(
        IBlobContainerService           blobs,
        IEncryptionService              encryption,
        IChunkIndexService              index,
        IChunkStorageService            chunkStorage,
        IFileTreeService                fileTreeService,
        ISnapshotService                snapshotSvc,
        IMediator                       mediator,
        ILogger<ArchiveCommandHandler>  logger,
        ILoggerFactory                  loggerFactory,
        string                          accountName,
        string                          containerName)
        : this(blobs, encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, logger, loggerFactory, accountName, containerName, OpenStagingSessionAsync)
    {
    }

    internal ArchiveCommandHandler(
        IBlobContainerService           blobs,
        IEncryptionService              encryption,
        IChunkIndexService              index,
        IChunkStorageService            chunkStorage,
        IFileTreeService                fileTreeService,
        ISnapshotService                snapshotSvc,
        IMediator                       mediator,
        ILogger<ArchiveCommandHandler>  logger,
        ILoggerFactory                  loggerFactory,
        string                          accountName,
        string                          containerName,
        Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>> openStagingSession)
    {
        _blobs              = blobs;
        _encryption         = encryption;
        _chunkIndex         = index;
        _chunkStorage       = chunkStorage;
        _fileTreeService    = fileTreeService;
        _snapshotSvc        = snapshotSvc;
        _mediator           = mediator;
        _logger             = logger;
        _loggerFactory      = loggerFactory;
        _accountName        = accountName;
        _containerName      = containerName;
        _openStagingSession = openStagingSession;
    }

    private static async Task<IFileTreeStagingSession> OpenStagingSessionAsync(LocalDirectory fileTreeCacheDirectory, CancellationToken cancellationToken)
        => await FileTreeStagingSession.OpenAsync(fileTreeCacheDirectory, cancellationToken);

    /// <summary>
    /// Executes the end-to-end archive pipeline for the provided command.
    /// </summary>
    /// <remarks>
    /// The pipeline enumerates files under the command's root directory, computes content hashes (or reuses pointer hashes),
    /// deduplicates against the persistent index and in-run uploads, uploads new chunks (large files directly, small files in tar bundles),
    /// and writes staged filetree entries. Once every stage drains, it validates the filetrees, flushes the chunk index while
    /// building the tree (concurrently), creates a snapshot, and finally writes pointer files and removes local binaries (concurrently,
    /// each gated on its flag). Progress and events are published via the mediator and operational details are recorded in the index
    /// and staged filetree. See the type-level documentation for the full stage/channel/event breakdown.
    /// </remarks>
    /// <param name="command">The archive command containing options (root directory, thresholds, flags) and parameters for the run.</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing pipeline operations.</param>
    /// <returns>
    /// An <see cref="ArchiveResult"/> with the operation outcome and metrics: on success, the scanned/uploaded/deduped counts,
    /// total size processed, and the snapshot root hash and timestamp; on failure, the counters collected so far and an error message.
    /// </returns>
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        var opts = command.CommandOptions;

        // ── Operation start marker (task 3.10) ───────────────────────────────
        _logger.LogInformation("[archive] Start: src={RootDir} account={Account} container={Container} tier={Tier} removeLocal={RemoveLocal} noPointers={NoPointers}", opts.RootDirectory, _accountName, _containerName, opts.UploadTier, opts.RemoveLocal, opts.NoPointers);

        // ── Ensure container exists ───────────────────────────────────────────
        _logger.LogInformation("[phase] ensure-container");
        await _blobs.CreateContainerIfNotExistsAsync(cancellationToken);

        // Validate options (task 8.13)
        if (opts is { RemoveLocal: true, NoPointers: true })
            return new ArchiveResult
            {
                Success               = false,
                FilesScanned          = 0,
                FilesUploaded         = 0,
                FilesDeduped          = 0,
                OriginalSize          = 0,
                IncrementalSize       = 0,
                IncrementalStoredSize = 0,
                RootHash              = null,
                SnapshotTime          = DateTimeOffset.UtcNow,
                ErrorMessage          = "--remove-local cannot be combined with --no-pointers"
            };

        // ── Shared state ──────────────────────────────────────────────────────

        long filesScanned    = 0;
        long filesUploaded   = 0;
        long filesDeduped    = 0;
        long originalSize          = 0;   // sum of original (uncompressed) sizes of ALL files in the snapshot
        long incrementalSize       = 0;   // original (uncompressed) bytes newly uploaded this run
        long incrementalStoredSize = 0;   // stored (compressed) bytes newly written to storage this run

        var stagingCacheDirectory = RepositoryLocalStatePaths.GetFileTreeCacheRoot(_accountName, _containerName);
        var fs = new RelativeFileSystem(LocalDirectory.Parse(opts.RootDirectory));
        IFileTreeStagingSession stagingSession;

        FileTreeHash? snapshotRootHash = null;
        var snapshotTime = DateTimeOffset.UtcNow;

        try
        {
            _logger.LogInformation("[phase] open-staging");
            stagingSession = await _openStagingSession(stagingCacheDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive pipeline failed");
            return new ArchiveResult
            {
                Success               = false,
                FilesScanned          = filesScanned,
                FilesUploaded         = filesUploaded,
                FilesDeduped          = filesDeduped,
                OriginalSize          = originalSize,
                IncrementalSize       = incrementalSize,
                IncrementalStoredSize = incrementalStoredSize,
                RootHash              = snapshotRootHash,
                SnapshotTime          = snapshotRootHash is null ? DateTimeOffset.UtcNow : snapshotTime,
                ErrorMessage          = ex.Message
            };
        }

        try
        {
            await using var session         = stagingSession;
            using var       stagingWriter   = new FileTreeStagingWriter(stagingSession.StagingRoot);
            var             pendingPointers = new ConcurrentBag<PendingPointerWrite>();
            var             pendingDeletes  = new ConcurrentBag<RelativePath>();

            // In-flight set: content hashes already queued/uploaded in this run
            // Used by the dedup stage to detect duplicates within the same run before the
            // index is updated. Value = original (uncompressed) size of the chunk, so a
            // pointer-only file referencing an in-run-uploaded chunk can be sized.
            var inFlightHashes = new ConcurrentDictionary<ContentHash, long>();

            // Channels between stages
            var filePairChannel        = Channel.CreateBounded<FilePair>(ChannelCapacity); // TODO be more specific about SingleWriter / MultipleReader etc
            var hashedChannel          = Channel.CreateUnbounded<HashedFilePair>();
            var largeChannel           = Channel.CreateUnbounded<FileToUpload>();
            var smallChannel           = Channel.CreateUnbounded<FileToUpload>();
            var sealedTarChannel       = Channel.CreateBounded<SealedTar>(TarUploadWorkers);
            var chunkIndexEntryChannel = Channel.CreateUnbounded<ShardEntry>();
            var fileTreeEntryChannel   = Channel.CreateUnbounded<HashedFilePair>();

            // ── Register queue-depth getters ──────────────────────
            opts.OnHashQueueReady?.Invoke(() => filePairChannel.Reader.Count);
            opts.OnUploadQueueReady?.Invoke(() => largeChannel.Reader.Count + sealedTarChannel.Reader.Count);

            // ── Stage 1: Enumerate ─────────────────────────────────
            _logger.LogInformation("[phase] enumerate");
            var enumTask = Task.Run(async () =>
            {
                try
                {
                    var  enumerator = new LocalFileEnumerator(_loggerFactory.CreateLogger<LocalFileEnumerator>());
                    var  pairs      = enumerator.Enumerate(LocalDirectory.Parse(opts.RootDirectory));
                    long count      = 0;
                    long totalBytes = 0;

                    foreach (var pair in pairs)
                    {
                        count++;
                        var fileSize = pair.Binary is null ? 0L : fs.GetFileSize(pair.RelativePath);
                        totalBytes += fileSize;
                        await _mediator.Publish(new FileScannedEvent(pair.RelativePath, fileSize), cancellationToken);
                        await filePairChannel.Writer.WriteAsync(pair, cancellationToken);
                    }

                    Interlocked.Add(ref filesScanned, count);
                    await _mediator.Publish(new ScanCompleteEvent(count, totalBytes), cancellationToken);
                    _logger.LogInformation("[scan] Enumeration complete: {Count} file(s) found", count);
                }
                finally
                {
                    filePairChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 2: Hash ×N ───────────────────────────────────
            _logger.LogInformation("[phase] hash");
            var hashTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        filePairChannel.Reader.ReadAllAsync(cancellationToken),
                        new ParallelOptions { MaxDegreeOfParallelism = HashWorkers, CancellationToken = cancellationToken },
                        async (pair, ct) =>
                        {
                            try
                            {
                                var fileSize = pair.Binary is null ? 0L : fs.GetFileSize(pair.RelativePath);

                                await _mediator.Publish(new FileHashingEvent(pair.RelativePath, fileSize), ct);

                                ContentHash contentHash;
                                if (pair is { Binary: null, Pointer: { Hash: not null } })
                                {
                                    // Pointer-only: use pointer hash directly (no re-hash)
                                    contentHash = pair.Pointer.Hash.Value;
                                }
                                else if (pair.Binary is not null)
                                {
                                    await using var s  = fs.OpenRead(pair.RelativePath);
                                    var             p  = opts.CreateHashProgress?.Invoke(pair.RelativePath, fileSize) ?? new Progress<long>();
                                    await using var ps = new ProgressStream(s, p);
                                    contentHash = await _encryption.ComputeHashAsync(ps, ct);
                                }
                                else
                                {
                                    // No binary and no pointer hash → skip
                                    _logger.LogWarning("Skipping FilePair with neither binary nor pointer hash: {Path}", pair.RelativePath);
                                    await _mediator.Publish(new FileSkippedEvent(pair.RelativePath), ct);
                                    return;
                                }

                                await _mediator.Publish(new FileHashedEvent(pair.RelativePath, contentHash), ct);

                                _logger.LogInformation("[hash] {Path} -> {Hash} ({Size})", pair.RelativePath, contentHash.Short8, fileSize.Bytes().Humanize());

                                var metadataPath = pair.Binary?.Path ?? pair.Pointer?.Path ?? throw new InvalidOperationException($"FilePair '{pair.RelativePath}' must contain either a binary or pointer file.");
                                var (created, modified) = fs.GetTimestamps(metadataPath);

                                await hashedChannel.Writer.WriteAsync(new HashedFilePair(pair, contentHash, created, modified), ct);
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                // A single unreadable file (broken link, permission denied, deleted mid-run)
                                // must never fault this stage — that would stop draining filePairChannel and
                                // deadlock the bounded enumerate→hash producer. Log, clear the row, skip.
                                _logger.LogWarning(ex, "Skipping unreadable file during hashing: {Path}", pair.RelativePath);
                                await _mediator.Publish(new FileSkippedEvent(pair.RelativePath), ct);
                            }
                        });
                }
                finally
                {
                    hashedChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 3: Dedup (×1) + Router ─────────────────
            _logger.LogInformation("[phase] dedup-route");
            var dedupTask = Task.Run(async () =>
            {
                try
                {
                    // One batched chunk-index lookup per batch (groups by root, loads cold shards in parallel)
                    // instead of a remote round-trip per file; the in-run dedup decisions stay per item.
                    const int batchSize = 256;
                    await foreach (var batch in hashedChannel.Reader.ReadAllBatchesAsync(batchSize, cancellationToken))
                    {
                        var known = await _chunkIndex.LookupAsync(batch.Select(h => h.ContentHash), cancellationToken);

                        foreach (var hashed in batch)
                        {
                            var isKnown = known.ContainsKey(hashed.ContentHash);

                            // Check for pointer-only with missing chunk (task 8.5)
                            if (hashed.FilePair.Binary is null)
                            {
                                if (!isKnown && !inFlightHashes.ContainsKey(hashed.ContentHash))
                                {
                                    _logger.LogWarning("Pointer-only file references missing chunk, skipping: {Path}", hashed.FilePair.RelativePath);
                                    continue;
                                }

                                // Known dedup: add to manifest only. No local binary, so the
                                // original size comes from the chunk index (or the in-flight upload).
                                _logger.LogInformation("[dedup] {Path} -> hit (pointer-only)", hashed.FilePair.RelativePath);
                                await fileTreeEntryChannel.Writer.WriteAsync(hashed, cancellationToken);
                                Interlocked.Increment(ref filesDeduped);
                                var pointerSize = isKnown ? known[hashed.ContentHash].OriginalSize : inFlightHashes[hashed.ContentHash];
                                Interlocked.Add(ref originalSize, pointerSize);
                                continue;
                            }

                            if (isKnown || inFlightHashes.ContainsKey(hashed.ContentHash))
                            {
                                // Already in index OR already queued in this run → dedup hit
                                _logger.LogInformation("[dedup] {Path} -> hit ({Hash})", hashed.FilePair.RelativePath, hashed.ContentHash.Short8);
                                await fileTreeEntryChannel.Writer.WriteAsync(hashed, cancellationToken);
                                Interlocked.Increment(ref filesDeduped);
                                Interlocked.Add(ref originalSize, fs.GetFileSize(hashed.FilePair.RelativePath));
                            }
                            else
                            {
                                // Needs upload → mark in-flight, route by size
                                var fileSize = fs.GetFileSize(hashed.FilePair.RelativePath);
                                inFlightHashes.TryAdd(hashed.ContentHash, fileSize);
                                Interlocked.Add(ref originalSize, fileSize);
                                Interlocked.Add(ref incrementalSize, fileSize);
                                var upload = new FileToUpload(hashed, fileSize);
                                var route  = fileSize >= opts.SmallFileThreshold ? "large" : "small";
                                _logger.LogInformation("[dedup] {Path} -> new/{Route} ({Hash}, {Size})", hashed.FilePair.RelativePath, route, hashed.ContentHash.Short8, fileSize.Bytes().Humanize());

                                if (fileSize >= opts.SmallFileThreshold)
                                    await largeChannel.Writer.WriteAsync(upload, cancellationToken);
                                else
                                    await smallChannel.Writer.WriteAsync(upload, cancellationToken);
                            }
                        }
                    }
                }
                finally
                {
                    largeChannel.Writer.Complete();
                    smallChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 4a: Large file upload ×N ─────────────────────
            _logger.LogInformation("[phase] large-upload");
            var largeUploadTask = Parallel.ForEachAsync(
                largeChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = UploadWorkers, CancellationToken = cancellationToken },
                async (upload, ct) =>
                {
                    _logger.LogInformation("[upload] Start: {Path} ({Hash}, {Size})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash.Short8, upload.FileSize.Bytes().Humanize());

                    var largeChunkHash = ChunkHash.Parse(upload.HashedPair.ContentHash);
                    await _mediator.Publish(new ChunkUploadingEvent(largeChunkHash, upload.FileSize), ct);

                    // Only the local read is skippable: a file that became unreadable between hashing
                    // and upload is skipped rather than faulting the stage (which would deadlock the
                    // bounded large channel). Upload/index failures must propagate and fail the run.
                    Stream s;
                    try
                    {
                        s = fs.OpenRead(upload.HashedPair.FilePair.RelativePath);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Skipping file during large upload: {Path}", upload.HashedPair.FilePair.RelativePath);
                        await _mediator.Publish(new FileSkippedEvent(upload.HashedPair.FilePair.RelativePath), ct);
                        return;
                    }

                    await using (s)
                    {
                        var p              = opts.CreateUploadProgress?.Invoke(largeChunkHash, upload.FileSize);
                        var uploadResult   = await _chunkStorage.UploadLargeAsync(largeChunkHash, s, upload.FileSize, opts.UploadTier, p, ct);
                        var originalSize   = uploadResult.OriginalSize ?? upload.FileSize;
                        var storedSize     = uploadResult.StoredSize;

                        // Enqueue ShardEntry and FileTreeUpdate
                        // so no filesystem access is needed here.
                        await chunkIndexEntryChannel.Writer.WriteAsync(new ShardEntry(upload.HashedPair.ContentHash, largeChunkHash, originalSize, storedSize, opts.UploadTier), ct);
                        await fileTreeEntryChannel.Writer.WriteAsync(upload.HashedPair, ct);
                        Interlocked.Increment(ref filesUploaded);
                        // Only count bytes actually written this run; a pre-existing blob (recovered from a
                        // prior crashed/concurrent run) stores nothing new even though it reports a StoredSize.
                        if (!uploadResult.AlreadyExisted)
                            Interlocked.Add(ref incrementalStoredSize, storedSize);

                        await _mediator.Publish(new ChunkUploadedEvent(largeChunkHash, storedSize), ct);

                        _logger.LogInformation("[upload] Done: {Path} ({Hash}, orig={Orig}, stored={Stored})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash.Short8, upload.FileSize.Bytes().Humanize(), storedSize.Bytes().Humanize());
                    }
                });

            // ── Stage 4b: Tar builder ×1 ───────────────────────────
            _logger.LogInformation("[phase] tar-build");
            var tarBuilderTask = Task.Run(async () =>
            {
                await using var tarBuilder = new TarBuilder(
                    opts.TarTargetSize,
                    _encryption,
                    onBundleStarted: () => _mediator.Publish(new TarBundleStartedEvent(), cancellationToken),
                    onEntryAdded: async (contentHash, entryCount, currentSize) =>
                    {
                        await _mediator.Publish(new TarEntryAddedEvent(contentHash, entryCount, currentSize), cancellationToken);
                        _logger.LogDebug("[tar] Entry added: {Hash}, count={Count}, size={Size}", contentHash.Short8, entryCount, currentSize.Bytes().Humanize());
                    },
                    onBundleSealing: async sealedTar =>
                    {
                        await _mediator.Publish(new TarBundleSealingEvent(sealedTar.Entries.Count, sealedTar.UncompressedSize, sealedTar.Content.Count, sealedTar.TarHash, sealedTar.Entries.Select(e => e.ContentHash).ToList()), cancellationToken);
                        _logger.LogInformation("[tar] Sealed: {TarHash} {Count} file(s), {Size}", sealedTar.TarHash.Short8, sealedTar.Entries.Count, sealedTar.UncompressedSize.Bytes().Humanize());
                        foreach (var te in sealedTar.Entries)
                            _logger.LogInformation("[tar] Entry: {Path} ({Hash}, {Size})", te.HashedPair.FilePair.RelativePath, te.ContentHash.Short8, te.OriginalSize.Bytes().Humanize());
                    });
                try
                {
                    await foreach (var upload in smallChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        // Open the source first: a failed open must skip this file rather than fault the
                        // builder (which would deadlock the bounded small/sealed-tar channels) or leave a
                        // half-written entry in the current bundle.
                        Stream source;
                        try
                        {
                            source = fs.OpenRead(upload.HashedPair.FilePair.RelativePath);
                        }
                        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning(ex, "Skipping unreadable file during tar build: {Path}", upload.HashedPair.FilePair.RelativePath);
                            await _mediator.Publish(new FileSkippedEvent(upload.HashedPair.FilePair.RelativePath), cancellationToken);
                            continue;
                        }

                        if (await tarBuilder.AddAsync(upload, source, cancellationToken) is { } sealedTar)
                            await sealedTarChannel.Writer.WriteAsync(sealedTar, cancellationToken);
                    }

                    // Seal the final partial bundle.
                    if (await tarBuilder.SealAsync(cancellationToken) is { } finalTar)
                        await sealedTarChannel.Writer.WriteAsync(finalTar, cancellationToken);
                }
                finally
                {
                    // The builder's DisposeAsync (await using) discards any half-built bundle; the sealed-tar
                    // channel is owned by this handler, so we complete it here — like every other pipeline channel.
                    sealedTarChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 4c: Tar upload ×N ────────────────────────────
            _logger.LogInformation("[phase] tar-upload");
            var tarUploadTask = Parallel.ForEachAsync(
                sealedTarChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = TarUploadWorkers, CancellationToken = cancellationToken },
                async (sealedTar, ct) =>
                {
                    // No local file is read here — the tar bytes are already sealed in memory. Every
                    // failure in this stage is a storage/index fault that must propagate and fail the
                    // run, so the rerun performs crash recovery rather than reporting a false success.
                    await _mediator.Publish(new ChunkUploadingEvent(sealedTar.TarHash, sealedTar.UncompressedSize), ct);

                    var             tarProgress    = opts.CreateUploadProgress?.Invoke(sealedTar.TarHash, sealedTar.UncompressedSize);
                    using var tarStream = new MemoryStream(sealedTar.Content.Array!, sealedTar.Content.Offset, sealedTar.Content.Count, writable: false, publiclyVisible: true);
                    var             uploadResult   = await _chunkStorage.UploadTarAsync(sealedTar.TarHash, tarStream, sealedTar.UncompressedSize, opts.UploadTier, tarProgress, ct);
                    var             storedSize     = uploadResult.StoredSize;
                    // One stored size per tar blob (its thin entries share the blob), so add it once here —
                    // but only when the blob was actually written this run, not recovered from a prior one.
                    if (!uploadResult.AlreadyExisted)
                        Interlocked.Add(ref incrementalStoredSize, storedSize);

                    // Parallel thin chunk creation for each entry
                    await Parallel.ForEachAsync(
                        sealedTar.Entries,
                        new ParallelOptions { MaxDegreeOfParallelism = ThinEntryWorkers, CancellationToken = ct },
                        async (entry, entryCt) =>
                        {
                            await _chunkStorage.UploadThinAsync(entry.ContentHash, sealedTar.TarHash, entry.OriginalSize, storedSize, entryCt);

                            // The tar blob's tier governs all of its thin entries.
                            await chunkIndexEntryChannel.Writer.WriteAsync(new ShardEntry(entry.ContentHash, sealedTar.TarHash, entry.OriginalSize, storedSize, opts.UploadTier), entryCt);
                            await fileTreeEntryChannel.Writer.WriteAsync(entry.HashedPair, entryCt);
                        });

                    await _mediator.Publish(new TarBundleUploadedEvent(sealedTar.TarHash, storedSize, sealedTar.Entries.Count), ct);
                    _logger.LogInformation("[tar] Uploaded: {TarHash} {Count} thin chunks, stored={Stored}", sealedTar.TarHash.Short8, sealedTar.Entries.Count, storedSize.Bytes().Humanize());
                    Interlocked.Add(ref filesUploaded, sealedTar.Entries.Count);
                });

            // ── Stage 5a: Update ChunkIndex ×1 ─────────────────────────────
            // Single consumer so writes funnel into batched SQLite transactions
            _logger.LogInformation("[phase] chunk-index-update");
            var chunkIndexUpdateTask = Task.Run(async () =>
            {
                const int batchSize = 256;
                var stagedTotal = 0;
                await foreach (var batch in chunkIndexEntryChannel.Reader.ReadAllBatchesAsync(batchSize, cancellationToken))
                {
                    _chunkIndex.AddEntries(batch);
                    stagedTotal += batch.Count;
                    _logger.LogDebug("[chunk-index] Staged batch of {Count} entries (total staged={StagedTotal})", batch.Count, stagedTotal);
                }
                _logger.LogInformation("[chunk-index] Staged {Count} chunk-index entries", stagedTotal);
            }, cancellationToken);

            // ── Stage 5b: Update FileTree + Pointer/Delete Intents ───────────────────────────────────
            _logger.LogInformation("[phase] filetree-update");
            var fileTreeUpdateTask = Parallel.ForEachAsync(
                fileTreeEntryChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = FileTreeUpdateWorkers, CancellationToken = cancellationToken },
                async (entry, ct) =>
                {
                    var pair = entry.FilePair;
                    await stagingWriter.AppendFileEntryAsync(pair.RelativePath, entry.ContentHash, entry.Created, entry.Modified, ct);

                    // Once the files are captured in the FileTree, we can write the pointers & delete them
                    if (!opts.NoPointers && pair.Binary is not null)
                        pendingPointers.Add(new PendingPointerWrite(pair.RelativePath, entry.ContentHash, entry.Created, entry.Modified));

                    if (opts.RemoveLocal && pair.Binary is not null)
                        pendingDeletes.Add(pair.RelativePath);
                });

            // Wait for all upload stages to complete
            _logger.LogInformation("[phase] await-workers");
            try
            {
                // Await all producers of the update channels before closing the writers
                await Task.WhenAll(largeUploadTask, tarBuilderTask, tarUploadTask, dedupTask);
            }
            finally
            {
                chunkIndexEntryChannel.Writer.Complete();
                fileTreeEntryChannel.Writer.Complete();
            }

            // Drain the local-state consumers, then the upstream stages.
            await Task.WhenAll(chunkIndexUpdateTask, fileTreeUpdateTask, hashTask, enumTask);

            // ── End-of-pipeline ───────────────────────────────────────────────

            // ── Stage 6a: Validate filetrees ──────────────────────────────────
            _logger.LogInformation("[phase] validate-filetrees");
            var fileTreeValidation = await _fileTreeService.ValidateAsync(cancellationToken);
            if (fileTreeValidation.SnapshotMismatch)
                _chunkIndex.InvalidateCaches();

            _logger.LogInformation("[phase] flush-chunkindex-and-synchronize-filetree");
            // ── Stage 6b: Flush chunk index (concurrent w/ 6c) ──
            var flushTask   = _chunkIndex.FlushAsync(cancellationToken);

            // ── Stage 6c: Build file tree (concurrent w/ 6b) ─────────────────────────────────────
            var treeBuilder = new FileTreeBuilder(_encryption, _fileTreeService, _loggerFactory.CreateLogger<FileTreeBuilder>());
            var treeTask = treeBuilder.SynchronizeAsync(stagingSession.StagingRoot, cancellationToken);

            await Task.WhenAll(flushTask, treeTask);

            var rootHash = await treeTask;
            _logger.LogInformation("[tree] Build complete: rootHash={RootHash}", rootHash?.Short8 ?? "(none)");

            // ── Stage 6d: Create snapshot ─────────────────────────────────────
            _logger.LogInformation("[phase] snapshot");
            if (rootHash is not null)
            {
                var latestSnapshot = await _snapshotSvc.ResolveAsync(cancellationToken: cancellationToken);
                if (latestSnapshot?.RootHash == rootHash)
                {
                    snapshotRootHash = latestSnapshot.RootHash;
                    snapshotTime     = latestSnapshot.Timestamp;
                    _logger.LogInformation("[snapshot] Unchanged: {Timestamp} rootHash={RootHash}", latestSnapshot.Timestamp.ToString("o"), latestSnapshot.RootHash.Short8);
                }
                else
                {
                    var snapshot = await _snapshotSvc.CreateAsync(rootHash.Value, filesScanned, originalSize, cancellationToken: cancellationToken);
                    snapshotRootHash = snapshot.RootHash;
                    snapshotTime     = snapshot.Timestamp;
                    await _chunkIndex.PromoteToSnapshotVersionAsync(BlobPaths.SnapshotPath(snapshot.Timestamp).Name.ToString());
                    _logger.LogInformation("[snapshot] Created: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

                    await _mediator.Publish(new SnapshotCreatedEvent(rootHash.Value, snapshot.Timestamp, snapshot.FileCount), cancellationToken);
                }
            }

            // ── Stage 6e: Write pointer files ×N in parallel (concurrent w/ 6f) ──────────────────
            _logger.LogInformation("[phase] write-pointers");
            var writePointersTask = opts.NoPointers
                ? Task.CompletedTask
                : Parallel.ForEachAsync(pendingPointers, cancellationToken, async (item, ct) =>
                {
                    try
                    {
                        await PointerFileFormat.WriteAsync(fs, item.BinaryPath, item.Hash, item.Created, item.Modified, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // A pointer write that fails for one file must not fault the whole stage.
                        _logger.LogWarning(ex, "Failed to write pointer file: {Path}", item.BinaryPath.ToPointerPath());
                    }
                });

            
            // ── Stage 6f: Remove local binary files ×N in parallel ────────────
            _logger.LogInformation("[phase] delete-local");
            var removeLocalTask = !opts.RemoveLocal
                ? Task.CompletedTask
                : Parallel.ForEachAsync(pendingDeletes, cancellationToken, (path, ct) =>
                {
                    try
                    {
                        fs.DeleteFile(path);
                    }
                    catch (Exception ex)
                    {
                        // A delete that fails for one file must not fault the whole stage.
                        _logger.LogWarning(ex, "Failed to delete local file: {Path}", path);
                    }
                    return ValueTask.CompletedTask;
                });

            await Task.WhenAll(writePointersTask, removeLocalTask);

            _logger.LogInformation("[phase] complete");

            _logger.LogInformation("[archive] Done: scanned={Scanned} uploaded={Uploaded} deduped={Deduped} uploadedSize={IncrementalSize} storedSize={IncrementalStoredSize} originalSize={OriginalSize} snapshot={Snapshot}", filesScanned, filesUploaded, filesDeduped, incrementalSize.Bytes().Humanize(), incrementalStoredSize.Bytes().Humanize(), originalSize.Bytes().Humanize(), snapshotTime.ToString("o"));

            return new ArchiveResult
            {
                Success               = true,
                FilesScanned          = filesScanned,
                FilesUploaded         = filesUploaded,
                FilesDeduped          = filesDeduped,
                OriginalSize          = originalSize,
                IncrementalSize       = incrementalSize,
                IncrementalStoredSize = incrementalStoredSize,
                RootHash              = snapshotRootHash,
                SnapshotTime          = snapshotTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive pipeline failed");
            return new ArchiveResult
            {
                Success               = false,
                FilesScanned          = filesScanned,
                FilesUploaded         = filesUploaded,
                FilesDeduped          = filesDeduped,
                OriginalSize          = originalSize,
                IncrementalSize       = incrementalSize,
                IncrementalStoredSize = incrementalStoredSize,
                RootHash              = snapshotRootHash,
                SnapshotTime          = snapshotRootHash is not null ? snapshotTime : DateTimeOffset.UtcNow,
                ErrorMessage          = ex.Message
            };
        }
    }
}
