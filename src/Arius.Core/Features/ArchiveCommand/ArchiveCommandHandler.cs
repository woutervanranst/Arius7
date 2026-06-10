using System.Collections.Concurrent;
using System.Threading.Channels;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
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
/// 2. **Hash** (×`HashWorkers`) — re-hash binaries (or reuse the pointer hash); skip unreadable files.
/// 3. **Dedup + Router** (×1) — look up the chunk index + the in-run `inFlightHashes` set:
///    - **hit** → dedup (emit only a filetree update; no upload, no index entry)
///    - **new** → route by size vs `opts.SmallFileThreshold` → large or small upload
/// 4. **Upload** (runs concurrently):
///    - **4a. Large Upload** (×`UploadWorkers`) — one chunk per file via `UploadLargeAsync`.
///    - **4b. Tar Builder** (×1) — pack small files into bundles of `opts.TarTargetSize`.
///    - **4c. Tar Upload** (×`TarUploadWorkers`) — upload the bundle, then fan out
///      `ThinEntryWorkers`=64 thin-chunk uploads, one per entry.
/// 5. **Local-state consumers** (drain what 3/4a/4c emit):
///    - **5a. Chunk-index** (×1) — batch 256 entries into single-writer SQLite transactions.
///    - **5b. Filetree** (×`UpdateWorkers`) — append staging entries (stripe-locked writer) and
///      collect `pendingPointers` / `pendingDeletes`.
/// 6. **End-of-pipeline** (sequential, after all stages above drain):
///    - **6a. Validate filetrees** (×1) — `ValidateAsync`; invalidate index caches on snapshot mismatch.
///    - **6b. Flush chunk index** — `_chunkIndex.FlushAsync`; runs concurrently with 6c (`Task.WhenAll`).
///    - **6c. Build file tree** — `FileTreeBuilder.SynchronizeAsync`; runs concurrently with 6b and yields the snapshot root hash.
///    - **6d. Create snapshot** (×1) — create + promote a snapshot for the root hash (skipped if unchanged).
///    - **6e. Write pointers** (×N) — write `pendingPointers` in parallel (unless `--no-pointers`).
///    - **6f. Remove local** (×1) — delete `pendingDeletes` (only if `--remove-local`).
///
/// ```
/// Enumerate ─► Hash ─► Dedup+Router ─┬─► Large Upload ───────────────┐
///                                    └─► Tar Builder ─► Tar Upload ──┤
///                                                                    ├─► Chunk-index consumer ─┐                             ┌─► flush chunk index (6b) ─┐
///                                                                    └─► Filetree consumer ────┴──► validate filetrees (6a) ─┤                           ├─► create snapshot (6d) ─► write pointers (6e) ─► remove local binaries (6f)
///                                                                                                                            └─► build file tree (6c) ───┘
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
/// | `fileTreeEntryChannel`   | Dedup + Upload (3/4a/4c)     | Filetree consumer (5b)     | unbounded       | `FileTreeUpdate` records (dedup hits emit here only).       |
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
    private const int TarUploadWorkers = 1;
    private const int ThinEntryWorkers = 64;
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
    /// writes staged filetree entries, builds a tree and creates a snapshot, and optionally writes pointer files and removes local binaries.
    /// Progress and events are published via the mediator and operational details are recorded in the index and staged filetree.
    /// </remarks>
    /// <param name="command">The archive command containing options (root directory, thresholds, flags) and parameters for the run.</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing pipeline operations.</param>
    /// <returns>
    /// An ArchiveResult containing success status, counts for scanned/uploaded/deduped files, total size processed,
    /// snapshot root hash and timestamp when created, and an error message when the operation failed.
    /// <summary>
    /// Runs the archive pipeline for the given command, processing files under the command's root directory into blob storage and producing an archive snapshot.
    /// </summary>
    /// <param name="command">The archive command containing options that control enumeration, hashing, deduplication, upload behavior, pointer writing, and local deletion.</param>
    /// <param name="cancellationToken">Token to observe while waiting for pipeline operations to complete.</param>
    /// <returns>An <see cref="ArchiveResult"/> with operation outcome and metrics: on success contains scanned/uploaded/deduped counts, total size, optional snapshot root hash and snapshot time; on failure contains collected counters so far and an error message.</returns>
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
                Success       = false,
                FilesScanned  = 0,
                FilesUploaded = 0,
                FilesDeduped  = 0,
                TotalSize     = 0,
                RootHash      = null,
                SnapshotTime  = DateTimeOffset.UtcNow,
                ErrorMessage  = "--remove-local cannot be combined with --no-pointers"
            };

        // ── Shared state ──────────────────────────────────────────────────────

        long filesScanned  = 0;
        long filesUploaded = 0;
        long filesDeduped  = 0;
        long totalSize     = 0;

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
                Success       = false,
                FilesScanned  = filesScanned,
                FilesUploaded = filesUploaded,
                FilesDeduped  = filesDeduped,
                TotalSize     = totalSize,
                RootHash      = snapshotRootHash,
                SnapshotTime  = snapshotRootHash is null ? DateTimeOffset.UtcNow : snapshotTime,
                ErrorMessage  = ex.Message
            };
        }

        try
        {
            await using var session         = stagingSession;
            using var       stagingWriter   = new FileTreeStagingWriter(stagingSession.StagingRoot);
            var             pendingPointers = new ConcurrentBag<(RelativePath Path, ContentHash Hash)>();
            var             pendingDeletes  = new ConcurrentBag<RelativePath>();

            // In-flight set: content hashes already queued/uploaded in this run
            // Used by the dedup stage to detect duplicates within the same run before the
            // index is updated.
            var inFlightHashes = new ConcurrentDictionary<ContentHash, bool>();

            // Channels between stages
            var filePairChannel        = Channel.CreateBounded<FilePair>(ChannelCapacity);
            var hashedChannel          = Channel.CreateUnbounded<HashedFilePair>();
            var largeChannel           = Channel.CreateUnbounded<FileToUpload>();
            var smallChannel           = Channel.CreateUnbounded<FileToUpload>();
            var sealedTarChannel       = Channel.CreateBounded<SealedTar>(TarUploadWorkers);
            var chunkIndexEntryChannel = Channel.CreateUnbounded<ShardEntry>();
            var fileTreeEntryChannel   = Channel.CreateUnbounded<FileTreeUpdate>();

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

                                await hashedChannel.Writer.WriteAsync(new HashedFilePair(pair, contentHash), ct);
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
                    await foreach (var hashed in hashedChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        var isKnown = await _chunkIndex.LookupAsync(hashed.ContentHash, cancellationToken) is not null;

                        // Check for pointer-only with missing chunk (task 8.5)
                        if (hashed.FilePair.Binary is null)
                        {
                            if (!isKnown && !inFlightHashes.ContainsKey(hashed.ContentHash))
                            {
                                _logger.LogWarning("Pointer-only file references missing chunk, skipping: {Path}", hashed.FilePair.RelativePath);
                                continue;
                            }

                            // Known dedup: add to manifest only
                            _logger.LogInformation("[dedup] {Path} -> hit (pointer-only)", hashed.FilePair.RelativePath);
                            var (created, modified) = ReadTimestamps(hashed, fs);
                            await fileTreeEntryChannel.Writer.WriteAsync(new FileTreeUpdate(hashed, created, modified), cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            continue;
                        }

                        if (isKnown || inFlightHashes.ContainsKey(hashed.ContentHash))
                        {
                            // Already in index OR already queued in this run → dedup hit
                            _logger.LogInformation("[dedup] {Path} -> hit ({Hash})", hashed.FilePair.RelativePath, hashed.ContentHash.Short8);
                            var (created, modified) = ReadTimestamps(hashed, fs);
                            await fileTreeEntryChannel.Writer.WriteAsync(new FileTreeUpdate(hashed, created, modified), cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            if (!opts.NoPointers)
                                pendingPointers.Add((hashed.FilePair.RelativePath, hashed.ContentHash));
                        }
                        else
                        {
                            // Needs upload → mark in-flight, route by size
                            inFlightHashes.TryAdd(hashed.ContentHash, true);
                            var fileSize = fs.GetFileSize(hashed.FilePair.RelativePath);
                            Interlocked.Add(ref totalSize, fileSize);
                            var upload = new FileToUpload(hashed, fileSize);
                            var route  = fileSize >= opts.SmallFileThreshold ? "large" : "small";
                            _logger.LogInformation("[dedup] {Path} -> new/{Route} ({Hash}, {Size})",
                                hashed.FilePair.RelativePath, route, hashed.ContentHash.Short8,
                                fileSize.Bytes().Humanize());

                            if (fileSize >= opts.SmallFileThreshold)
                                await largeChannel.Writer.WriteAsync(upload, cancellationToken);
                            else
                                await smallChannel.Writer.WriteAsync(upload, cancellationToken);
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
                        var compressedSize = uploadResult.StoredSize;

                        // Enqueue ShardEntry and FileTreeUpdate
                        var (created, modified) = ReadTimestamps(upload.HashedPair, fs);
                        await chunkIndexEntryChannel.Writer.WriteAsync(new ShardEntry(upload.HashedPair.ContentHash, largeChunkHash, originalSize, compressedSize), ct);
                        await fileTreeEntryChannel.Writer.WriteAsync(new FileTreeUpdate(upload.HashedPair, created, modified), ct);
                        Interlocked.Increment(ref filesUploaded);

                        if (!opts.NoPointers)
                            pendingPointers.Add((upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash));

                        if (opts.RemoveLocal)
                            pendingDeletes.Add(upload.HashedPair.FilePair.RelativePath);

                        await _mediator.Publish(new ChunkUploadedEvent(largeChunkHash, compressedSize), ct);

                        _logger.LogInformation("[upload] Done: {Path} ({Hash}, orig={Orig}, compressed={Compressed})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash.Short8, upload.FileSize.Bytes().Humanize(), compressedSize.Bytes().Humanize());
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
                    var             compressedSize = uploadResult.StoredSize;

                    var proportionalFactor = sealedTar.UncompressedSize > 0
                        ? (double)compressedSize / sealedTar.UncompressedSize
                        : 1.0;

                    // Parallel thin chunk creation for each entry
                    var shardEntries = new ConcurrentBag<ShardEntry>();
                    await Parallel.ForEachAsync(
                        sealedTar.Entries,
                        new ParallelOptions { MaxDegreeOfParallelism = ThinEntryWorkers, CancellationToken = ct },
                        async (entry, entryCt) =>
                        {
                            var proportional = (long)(entry.OriginalSize * proportionalFactor);
                            await _chunkStorage.UploadThinAsync(entry.ContentHash, sealedTar.TarHash, entry.OriginalSize, proportional, entryCt);

                            var (created, modified) = ReadTimestamps(entry.HashedPair, fs);
                            await chunkIndexEntryChannel.Writer.WriteAsync(new ShardEntry(entry.ContentHash, sealedTar.TarHash, entry.OriginalSize, proportional), entryCt);
                            await fileTreeEntryChannel.Writer.WriteAsync(new FileTreeUpdate(entry.HashedPair, created, modified), entryCt);
                        });
                    
                    // Batch add for new entries. Only runs if the whole loop completes, preserving the "index entry implies blob exists" invariant.
                    _chunkIndex.AddEntries(shardEntries);

                    await _mediator.Publish(new TarBundleUploadedEvent(sealedTar.TarHash, compressedSize, sealedTar.Entries.Count), ct);
                    _logger.LogInformation("[tar] Uploaded: {TarHash} {Count} thin chunks, compressed={Compressed}", sealedTar.TarHash.Short8, sealedTar.Entries.Count, compressedSize.Bytes().Humanize());
                    Interlocked.Add(ref filesUploaded, sealedTar.Entries.Count);
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
            // ── Stage 6b: Flush chunk index ──
            var flushTask   = _chunkIndex.FlushAsync(cancellationToken);

            // ── Stage 6c: Build file tree ─────────────────────────────────────
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
                    var snapshot = await _snapshotSvc.CreateAsync(rootHash.Value, filesScanned, totalSize, cancellationToken: cancellationToken);
                    snapshotRootHash = snapshot.RootHash;
                    snapshotTime     = snapshot.Timestamp;
                    await _chunkIndex.PromoteToSnapshotVersionAsync(BlobPaths.SnapshotPath(snapshot.Timestamp).Name.ToString());
                    _logger.LogInformation("[snapshot] Created: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

                    await _mediator.Publish(new SnapshotCreatedEvent(rootHash.Value, snapshot.Timestamp, snapshot.FileCount), cancellationToken);
                }
            }

            // ── Stage 6e: Write pointer files ×N in parallel ──────────────────
            _logger.LogInformation("[phase] write-pointers");
            if (!opts.NoPointers)
            {
                await Parallel.ForEachAsync(pendingPointers, cancellationToken, async (item, ct) =>
                {
                    var (path, hash) = item;
                    try
                    {
                        await fs.WriteAllTextAsync(path.ToPointerPath(), hash.ToString(), ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // A pointer write that fails for one file must not fault the whole stage.
                        _logger.LogWarning(ex, "Failed to write pointer file: {Path}", path.ToPointerPath());
                    }
                });
            }

            // ── Stage 6f: Remove local binary files ───────────────────────────
            _logger.LogInformation("[phase] delete-local");
            if (opts.RemoveLocal)
            {
                foreach (var path in pendingDeletes)
                {
                    try
                    {
                        fs.DeleteFile(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete local file: {Path}", path);
                    }
                }
            }

            _logger.LogInformation("[phase] complete");

            _logger.LogInformation("[archive] Done: scanned={Scanned} uploaded={Uploaded} deduped={Deduped} size={Size} snapshot={Snapshot}", filesScanned, filesUploaded, filesDeduped, totalSize.Bytes().Humanize(), snapshotTime.ToString("o"));

            return new ArchiveResult
            {
                Success       = true,
                FilesScanned  = filesScanned,
                FilesUploaded = filesUploaded,
                FilesDeduped  = filesDeduped,
                TotalSize     = totalSize,
                RootHash      = snapshotRootHash,
                SnapshotTime  = snapshotTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive pipeline failed");
            return new ArchiveResult
            {
                Success       = false,
                FilesScanned  = filesScanned,
                FilesUploaded = filesUploaded,
                FilesDeduped  = filesDeduped,
                TotalSize     = totalSize,
                RootHash      = snapshotRootHash,
                SnapshotTime  = snapshotRootHash is not null ? snapshotTime : DateTimeOffset.UtcNow,
                ErrorMessage  = ex.Message
            };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (DateTimeOffset Created, DateTimeOffset Modified) ReadTimestamps(HashedFilePair hashed, RelativeFileSystem fileSystem)
    {
        var pair = hashed.FilePair;
        var metadataPath = pair.Binary?.Path ?? pair.Pointer?.Path ?? throw new InvalidOperationException($"FilePair '{pair.RelativePath}' must contain either a binary or pointer file.");

        return fileSystem.GetTimestamps(metadataPath);
    }
}
