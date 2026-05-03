using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Threading.Channels;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.LocalFile;
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
/// Pipeline stages (tasks 8.2 – 8.14):
/// <code>
/// Enumerate (×1) → Hash (×N) → Dedup (×1) → Router → Large Upload (×N)
///                                                     → Tar Builder (×1) → Tar Upload (×N)
///                                → Filetree staging writer (disk)
/// End-of-pipeline: Index Flush → Tree Build → Snapshot → Pointer Write → Remove Local
/// </code>
/// </summary>
public sealed class ArchiveCommandHandler : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    // ── Concurrency knobs ─────────────────────────────────────────────────────

    private const int HashWorkers     = 4;
    private const int UploadWorkers   = 4;
    private const int ChannelCapacity = 64;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobContainerService          _blobs;
    private readonly IEncryptionService             _encryption;
    private readonly ChunkIndexService              _chunkIndex;
    private readonly IChunkStorageService           _chunkStorage;
    private readonly FileTreeService               _fileTreeService;
    private readonly SnapshotService                _snapshotSvc;
    private readonly IMediator                      _mediator;
    private readonly ILogger<ArchiveCommandHandler> _logger;
    private readonly string                         _accountName;
    private readonly string                         _containerName;
    private readonly Func<string, CancellationToken, Task<IFileTreeStagingSession>> _openStagingSession;

    public ArchiveCommandHandler(
        IBlobContainerService           blobs,
        IEncryptionService              encryption,
        ChunkIndexService               index,
        IChunkStorageService            chunkStorage,
        FileTreeService                 fileTreeService,
        SnapshotService                 snapshotSvc,
        IMediator                       mediator,
        ILogger<ArchiveCommandHandler>  logger,
        string                          accountName,
        string                          containerName)
        : this(blobs, encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, logger, accountName, containerName, OpenStagingSessionAsync)
    {
    }

    internal ArchiveCommandHandler(
        IBlobContainerService           blobs,
        IEncryptionService              encryption,
        ChunkIndexService               index,
        IChunkStorageService            chunkStorage,
        FileTreeService                 fileTreeService,
        SnapshotService                 snapshotSvc,
        IMediator                       mediator,
        ILogger<ArchiveCommandHandler>  logger,
        string                          accountName,
        string                          containerName,
        Func<string, CancellationToken, Task<IFileTreeStagingSession>> openStagingSession)
    {
        _blobs           = blobs;
        _encryption      = encryption;
        _chunkIndex      = index;
        _chunkStorage    = chunkStorage;
        _fileTreeService = fileTreeService;
        _snapshotSvc     = snapshotSvc;
        _mediator        = mediator;
        _logger          = logger;
        _accountName     = accountName;
        _containerName   = containerName;
        _openStagingSession = openStagingSession;
    }

    private static async Task<IFileTreeStagingSession> OpenStagingSessionAsync(string fileTreeCacheDirectory, CancellationToken cancellationToken)
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

        var stagingCacheDirectory = RepositoryPaths.GetFileTreeCacheDirectory(_accountName, _containerName);
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
            var             pendingPointers = new ConcurrentBag<(string FullPath, ContentHash Hash)>();
            var             pendingDeletes  = new ConcurrentBag<string>();

            // In-flight set: content hashes already queued/uploaded in this run (task 4.8)
            // Used by the dedup stage to detect duplicates within the same run before the
            // index is updated.
            var inFlightHashes = new ConcurrentDictionary<ContentHash, bool>();

            // Channels between stages (task 8.2)
            var filePairChannel   = Channel.CreateBounded<FilePair>(ChannelCapacity);
            var hashedChannel     = Channel.CreateBounded<HashedFilePair>(ChannelCapacity);
            var largeChannel      = Channel.CreateBounded<FileToUpload>(ChannelCapacity);
            var smallChannel      = Channel.CreateBounded<FileToUpload>(ChannelCapacity);
            var sealedTarChannel  = Channel.CreateBounded<SealedTar>(ChannelCapacity / 2);
            var indexEntryChannel = Channel.CreateUnbounded<IndexEntry>();

            // ── Register queue-depth getters (task 2.3) ──────────────────────
            opts.OnHashQueueReady?.Invoke(() => filePairChannel.Reader.Count);
            opts.OnUploadQueueReady?.Invoke(() => largeChannel.Reader.Count + sealedTarChannel.Reader.Count);

            // ── Stage 1: Enumerate (task 8.3) ─────────────────────────────────
            _logger.LogInformation("[phase] enumerate");
            var enumTask = Task.Run(async () =>
            {
                try
                {
                    var  enumerator = new LocalFileEnumerator(_logger as ILogger<LocalFileEnumerator>);
                    var  pairs      = enumerator.Enumerate(opts.RootDirectory);
                    long count      = 0;
                    long totalBytes = 0;

                    foreach (var pair in pairs)
                    {
                        count++;
                        var fullPath = pair.BinaryExists
                            ? Path.Combine(opts.RootDirectory, pair.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                            : null;
                        var fileSize = fullPath is not null && File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;
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

            // ── Stage 2: Hash ×N (task 8.4) ───────────────────────────────────
            _logger.LogInformation("[phase] hash");
            var hashTask = Parallel.ForEachAsync(
                    filePairChannel.Reader.ReadAllAsync(cancellationToken),
                    new ParallelOptions { MaxDegreeOfParallelism = HashWorkers, CancellationToken = cancellationToken },
                    async (pair, ct) =>
                    {
                        var fullBinaryPath = pair.BinaryExists
                            ? Path.Combine(opts.RootDirectory, pair.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                            : null;
                        var fileSize = fullBinaryPath is not null ? new FileInfo(fullBinaryPath).Length : 0L;

                        await _mediator.Publish(new FileHashingEvent(pair.RelativePath, fileSize), ct);

                        ContentHash contentHash;
                        if (pair is { BinaryExists: false, PointerHash: not null })
                        {
                            // Pointer-only: use pointer hash directly (no re-hash)
                            contentHash = pair.PointerHash.Value;
                        }
                        else if (pair.BinaryExists)
                        {
                            await using var fs           = File.OpenRead(fullBinaryPath!);
                            var             hashProgress = opts.CreateHashProgress?.Invoke(pair.RelativePath, fileSize) ?? new Progress<long>();
                            await using var ps           = new ProgressStream(fs, hashProgress);
                            contentHash = await _encryption.ComputeHashAsync(ps, ct);
                        }
                        else
                        {
                            // No binary and no pointer hash → skip
                            _logger.LogWarning("Skipping FilePair with neither binary nor pointer hash: {Path}", pair.RelativePath);
                            return;
                        }

                        await _mediator.Publish(new FileHashedEvent(pair.RelativePath, contentHash), ct);

                        _logger.LogInformation("[hash] {Path} -> {Hash} ({Size})", pair.RelativePath, contentHash.Short8, fileSize.Bytes().Humanize());

                        await hashedChannel.Writer.WriteAsync(new HashedFilePair(pair, contentHash, opts.RootDirectory), ct);
                    })
                .ContinueWith(_ => hashedChannel.Writer.Complete(), CancellationToken.None);

            // ── Stage 3: Dedup (×1) + Router (task 8.5, 8.6) ─────────────────
            _logger.LogInformation("[phase] dedup-route");
            var dedupTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var hashed in hashedChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        var isKnown = await _chunkIndex.LookupAsync(hashed.ContentHash, cancellationToken) is not null;

                        // Check for pointer-only with missing chunk (task 8.5)
                        if (!hashed.FilePair.BinaryExists)
                        {
                            if (!isKnown && !inFlightHashes.ContainsKey(hashed.ContentHash))
                            {
                                _logger.LogWarning("Pointer-only file references missing chunk, skipping: {Path}", hashed.FilePair.RelativePath);
                                continue;
                            }

                            // Known dedup: add to manifest only
                            _logger.LogInformation("[dedup] {Path} -> hit (pointer-only)", hashed.FilePair.RelativePath);
                            await WriteFileTreeEntry(hashed, opts.RootDirectory, stagingWriter, cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            continue;
                        }

                        if (isKnown || inFlightHashes.ContainsKey(hashed.ContentHash))
                        {
                            // Already in index OR already queued in this run → dedup hit
                            _logger.LogInformation("[dedup] {Path} -> hit ({Hash})", hashed.FilePair.RelativePath, hashed.ContentHash.Short8);
                            await WriteFileTreeEntry(hashed, opts.RootDirectory, stagingWriter, cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            if (!opts.NoPointers)
                                pendingPointers.Add((Path.Combine(opts.RootDirectory, hashed.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), hashed.ContentHash));
                        }
                        else
                        {
                            // Needs upload → mark in-flight, route by size
                            inFlightHashes.TryAdd(hashed.ContentHash, true);
                            var fileSize = hashed.FilePair.FileSize ?? 0;
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

            // ── Stage 4a: Large file upload ×N (task 8.7) ─────────────────────
            _logger.LogInformation("[phase] large-upload");
            var largeUploadTask = Parallel.ForEachAsync(
                largeChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = UploadWorkers, CancellationToken = cancellationToken },
                async (upload, ct) =>
                {
                    _logger.LogInformation("[upload] Start: {Path} ({Hash}, {Size})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash.Short8, upload.FileSize.Bytes().Humanize());

                    var largeChunkHash = ChunkHash.Parse(upload.HashedPair.ContentHash);
                    await _mediator.Publish(new ChunkUploadingEvent(largeChunkHash, upload.FileSize), ct);

                    var fullPath = Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                    await using var fs             = File.OpenRead(fullPath);
                    var             uploadProgress = opts.CreateUploadProgress?.Invoke(largeChunkHash, upload.FileSize);
                    var             uploadResult   = await _chunkStorage.UploadLargeAsync(largeChunkHash, fs, upload.FileSize, opts.UploadTier, uploadProgress, ct);
                    var             compressedSize = uploadResult.StoredSize;

                    var entry = new IndexEntry(upload.HashedPair.ContentHash, largeChunkHash, upload.FileSize, compressedSize);
                    _chunkIndex.AddEntry(new ShardEntry(entry.ContentHash, entry.ChunkHash, entry.OriginalSize, entry.CompressedSize));
                    await indexEntryChannel.Writer.WriteAsync(entry, ct);

                    await WriteFileTreeEntry(upload.HashedPair, opts.RootDirectory, stagingWriter, ct);
                    Interlocked.Increment(ref filesUploaded);

                    if (!opts.NoPointers)
                        pendingPointers.Add((Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), upload.HashedPair.ContentHash));

                    if (opts.RemoveLocal)
                        pendingDeletes.Add(Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                    await _mediator.Publish(new ChunkUploadedEvent(largeChunkHash, compressedSize), ct);

                    _logger.LogInformation("[upload] Done: {Path} ({Hash}, orig={Orig}, compressed={Compressed})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash.Short8, upload.FileSize.Bytes().Humanize(), compressedSize.Bytes().Humanize());
                });

            // ── Stage 4b: Tar builder ×1 (task 8.8) ───────────────────────────
            _logger.LogInformation("[phase] tar-build");
            var tarBuilderTask = Task.Run(async () =>
            {
                try
                {
                    var         tarEntries     = new List<TarEntry>();
                    string?     currentTarPath = null;
                    TarWriter?  tarWriter      = null;
                    FileStream? tarStream      = null;
                    long        currentSize    = 0;

                    async Task SealCurrentTar()
                    {
                        if (tarWriter is null) 
                            return;

                        await tarWriter.DisposeAsync();
                        tarStream!.Dispose();

                        // Compute tar hash
                        ChunkHash tarHash;
                        await using (var fs = File.OpenRead(currentTarPath!))
                        {
                            tarHash = ChunkHash.Parse(await _encryption.ComputeHashAsync(fs, cancellationToken));
                        }

                        await _mediator.Publish(new TarBundleSealingEvent(tarEntries.Count, currentSize, tarHash, tarEntries.Select(e => e.ContentHash).ToList()), cancellationToken);

                        _logger.LogInformation("[tar] Sealed: {TarHash} {Count} file(s), {Size}", tarHash.Short8, tarEntries.Count, currentSize.Bytes().Humanize());
                        foreach (var te in tarEntries)
                            _logger.LogInformation("[tar] Entry: {Path} ({Hash}, {Size})", te.HashedPair.FilePair.RelativePath, te.ContentHash.Short8, te.OriginalSize.Bytes().Humanize());

                        await sealedTarChannel.Writer.WriteAsync(new SealedTar(currentTarPath!, tarHash, currentSize, tarEntries.ToList()), cancellationToken);

                        tarEntries.Clear();
                        currentTarPath = null;
                        tarWriter      = null;
                        tarStream      = null;
                        currentSize    = 0;
                    }

                    await foreach (var upload in smallChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        // Initialize new tar if needed
                        if (tarWriter is null)
                        {
                            currentTarPath = Path.GetTempFileName();
                            tarStream      = new FileStream(currentTarPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                            tarWriter      = new TarWriter(tarStream, leaveOpen: false);
                            await _mediator.Publish(new TarBundleStartedEvent(), cancellationToken);
                        }

                        var fullPath = Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        // Write entry named by content-hash (not original path)
                        var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, upload.HashedPair.ContentHash.ToString());
                        await using (var fs = File.OpenRead(fullPath))
                        {
                            tarEntry.DataStream = fs;
                            await tarWriter.WriteEntryAsync(tarEntry, cancellationToken);
                        }

                        tarEntry.DataStream = null;

                        tarEntries.Add(new TarEntry(upload.HashedPair.ContentHash, upload.FileSize, upload.HashedPair));
                        currentSize += upload.FileSize;

                        await _mediator.Publish(new TarEntryAddedEvent(upload.HashedPair.ContentHash, tarEntries.Count, currentSize), cancellationToken);
                        _logger.LogDebug("[tar] Entry added: {Hash}, count={Count}, size={Size}", upload.HashedPair.ContentHash.Short8, tarEntries.Count, currentSize.Bytes().Humanize());

                        if (currentSize >= opts.TarTargetSize)
                            await SealCurrentTar();
                    }

                    // Seal final partial tar
                    await SealCurrentTar();
                }
                finally
                {
                    sealedTarChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 4c: Tar upload ×N (task 8.9) ────────────────────────────
            _logger.LogInformation("[phase] tar-upload");
            var tarUploadTask = Parallel.ForEachAsync(
                sealedTarChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = UploadWorkers, CancellationToken = cancellationToken },
                async (sealed_, ct) =>
                {
                    try
                    {
                        await _mediator.Publish(new ChunkUploadingEvent(sealed_.TarHash, sealed_.UncompressedSize), ct);

                        await using var fs             = File.OpenRead(sealed_.TarFilePath);
                        var             tarProgress    = opts.CreateUploadProgress?.Invoke(sealed_.TarHash, sealed_.UncompressedSize);
                        var             uploadResult   = await _chunkStorage.UploadTarAsync(sealed_.TarHash, fs, sealed_.UncompressedSize, opts.UploadTier, tarProgress, ct);
                        var             compressedSize = uploadResult.StoredSize;

                        var proportionalFactor = sealed_.UncompressedSize > 0
                            ? (double)compressedSize / sealed_.UncompressedSize
                            : 1.0;

                        // Create thin chunks for each entry
                        foreach (var entry in sealed_.Entries)
                        {
                            var proportional = (long)(entry.OriginalSize * proportionalFactor);
                            await _chunkStorage.UploadThinAsync(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional, ct);

                            _chunkIndex.AddEntry(new ShardEntry(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional));
                            await indexEntryChannel.Writer.WriteAsync(new IndexEntry(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional), ct);

                            await WriteFileTreeEntry(entry.HashedPair, opts.RootDirectory, stagingWriter, ct);

                            if (!opts.NoPointers)
                                pendingPointers.Add((Path.Combine(opts.RootDirectory, entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), entry.ContentHash));

                            if (opts.RemoveLocal)
                                pendingDeletes.Add(Path.Combine(opts.RootDirectory, entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        }

                        await _mediator.Publish(new TarBundleUploadedEvent(sealed_.TarHash, compressedSize, sealed_.Entries.Count), ct);
                        _logger.LogInformation("[tar] Uploaded: {TarHash} {Count} thin chunks, compressed={Compressed}", sealed_.TarHash.Short8, sealed_.Entries.Count, compressedSize.Bytes().Humanize());
                        Interlocked.Add(ref filesUploaded, sealed_.Entries.Count);
                    }
                    finally
                    {
                        try { File.Delete(sealed_.TarFilePath); } catch { /* ignore */ }
                    }
                });

            // Wait for all upload stages to complete
            _logger.LogInformation("[phase] await-workers");
            await largeUploadTask;
            await tarBuilderTask;
            await tarUploadTask;
            indexEntryChannel.Writer.Complete();
            await dedupTask;
            await enumTask;

            // ── End-of-pipeline ───────────────────────────────────────────────

            _logger.LogInformation("[phase] validate-filetrees");
            await _fileTreeService.ValidateAsync(cancellationToken);

            // Task 8.10: Index flush
            _logger.LogInformation("[phase] flush-index");
            await _chunkIndex.FlushAsync(cancellationToken);

            // Task 8.11: Build tree from staged entries → create snapshot
            _logger.LogInformation("[phase] build-filetree");
            var treeBuilder = new FileTreeBuilder(_encryption, _fileTreeService);
            var rootHash    = await treeBuilder.SynchronizeAsync(stagingSession.StagingRoot, cancellationToken);
            _logger.LogInformation("[tree] Build complete: rootHash={RootHash}", rootHash?.Short8 ?? "(none)");

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
                    _logger.LogInformation("[snapshot] Created: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash.Short8);

                    await _mediator.Publish(new SnapshotCreatedEvent(rootHash.Value, snapshot.Timestamp, snapshot.FileCount), cancellationToken);
                }
            }

            // Task 8.12: Write pointer files ×N in parallel
            _logger.LogInformation("[phase] write-pointers");
            if (!opts.NoPointers)
            {
                await Parallel.ForEachAsync(pendingPointers, cancellationToken, async (item, ct) =>
                {
                    var (fullPath, hash) = item;
                    var pointerPath = fullPath + ".pointer.arius";
                    await File.WriteAllTextAsync(pointerPath, hash.ToString(), ct);
                });
            }

            // Task 8.13: Remove local binary files
            _logger.LogInformation("[phase] delete-local");
            if (opts.RemoveLocal)
            {
                foreach (var path in pendingDeletes)
                {
                    try
                    {
                        File.Delete(path);
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

    private static async Task WriteFileTreeEntry(HashedFilePair hashed, string rootDir, FileTreeStagingWriter writer, CancellationToken ct)
    {
        var pair = hashed.FilePair;

        // Use binary metadata if available, otherwise pointer metadata
        DateTimeOffset created, modified;
        if (pair.BinaryExists)
        {
            var fullPath = Path.Combine(rootDir, pair.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var fi       = new FileInfo(fullPath);
            created  = new DateTimeOffset(fi.CreationTimeUtc,  TimeSpan.Zero);
            modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
        }
        else
        {
            created  = DateTimeOffset.UtcNow;
            modified = DateTimeOffset.UtcNow;
        }

        // Normalize the path (remove pointer suffix for pointer-only entries mapped to binary path)
        var fileTreePath = pair.RelativePath;
        if (!pair.BinaryExists && fileTreePath.EndsWith(".pointer.arius", StringComparison.OrdinalIgnoreCase))
            fileTreePath = fileTreePath[..^".pointer.arius".Length];

        await writer.AppendFileEntryAsync(fileTreePath, hashed.ContentHash, created, modified, ct);
    }

}
