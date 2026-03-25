using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.LocalFile;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Arius.Core.Streaming;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Threading.Channels;

namespace Arius.Core.Archive;

/// <summary>
/// Implements the full archive pipeline as a Mediator command handler.
///
/// Pipeline stages (tasks 8.2 – 8.14):
/// <code>
/// Enumerate (×1) → Hash (×N) → Dedup (×1) → Router → Large Upload (×N)
///                                                     → Tar Builder (×1) → Tar Upload (×N)
///                                → Manifest Writer (disk)
/// End-of-pipeline: Index Flush → External Sort → Tree Build → Snapshot → Pointer Write → Remove Local
/// </code>
/// </summary>
public sealed class ArchivePipelineHandler : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    // ── Concurrency knobs ─────────────────────────────────────────────────────

    private const int HashWorkers     = 4;
    private const int UploadWorkers   = 4;
    private const int ChannelCapacity = 64;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobStorageService             _blobs;
    private readonly IEncryptionService              _encryption;
    private readonly ChunkIndexService               _index;
    private readonly IMediator                       _mediator;
    private readonly ILogger<ArchivePipelineHandler> _logger;
    private readonly string                          _accountName;
    private readonly string                          _containerName;

    public ArchivePipelineHandler(
        IBlobStorageService             blobs,
        IEncryptionService              encryption,
        ChunkIndexService               index,
        IMediator                       mediator,
        ILogger<ArchivePipelineHandler> logger,
        string                          accountName,
        string                          containerName)
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
    /// Executes the end-to-end archive pipeline for the provided command.
    /// </summary>
    /// <remarks>
    /// The pipeline enumerates files under the command's root directory, computes content hashes (or reuses pointer hashes),
    /// deduplicates against the persistent index and in-run uploads, uploads new chunks (large files directly, small files in tar bundles),
    /// writes a manifest, builds a tree and creates a snapshot, and optionally writes pointer files and removes local binaries.
    /// Progress and events are published via the mediator and operational details are recorded in the index and manifest.
    /// </remarks>
    /// <param name="command">The archive command containing options (root directory, thresholds, flags) and parameters for the run.</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing pipeline operations.</param>
    /// <returns>
    /// An ArchiveResult containing success status, counts for scanned/uploaded/deduped files, total size processed,
    /// snapshot root hash and timestamp when created, and an error message when the operation failed.
    /// </returns>
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // ── Operation start marker (task 3.10) ───────────────────────────────
        _logger.LogInformation("[archive] Start: src={RootDir} account={Account} container={Container} tier={Tier} removeLocal={RemoveLocal} noPointers={NoPointers}", opts.RootDirectory, _accountName, _containerName, opts.UploadTier, opts.RemoveLocal, opts.NoPointers);

        // ── Ensure container exists ───────────────────────────────────────────
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

        var manifestPath    = Path.GetTempFileName();
        var manifestWriter  = new ManifestWriter(manifestPath);
        var pendingPointers = new ConcurrentBag<(string FullPath, string Hash)>();
        var pendingDeletes  = new ConcurrentBag<string>();

        // In-flight set: content hashes already queued/uploaded in this run (task 4.8)
        // Used by the dedup stage to detect duplicates within the same run before the
        // index is updated.
        var inFlightHashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Channels between stages (task 8.2)
        var filePairChannel   = Channel.CreateBounded<FilePair>(ChannelCapacity);
        var hashedChannel     = Channel.CreateBounded<HashedFilePair>(ChannelCapacity);
        var largeChannel      = Channel.CreateBounded<FileToUpload>(ChannelCapacity);
        var smallChannel      = Channel.CreateBounded<FileToUpload>(ChannelCapacity);
        var sealedTarChannel  = Channel.CreateBounded<SealedTar>(ChannelCapacity / 2);
        var indexEntryChannel = Channel.CreateUnbounded<IndexEntry>();

        try
        {
            // ── Stage 1: Enumerate (task 8.3) ─────────────────────────────────
            var enumTask = Task.Run(async () =>
            {
                try
                {
                    var enumerator = new LocalFileEnumerator(_logger as ILogger<LocalFileEnumerator>);
                    var pairs      = enumerator.Enumerate(opts.RootDirectory);
                    long count     = 0;

                    foreach (var pair in pairs)
                    {
                        count++;
                        await filePairChannel.Writer.WriteAsync(pair, cancellationToken);
                    }

                    Interlocked.Add(ref filesScanned, count);
                    await _mediator.Publish(new FileScannedEvent(count), cancellationToken);
                    _logger.LogInformation("[scan] Enumeration complete: {Count} file(s) found", count);
                }
                finally
                {
                    filePairChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 2: Hash ×N (task 8.4) ───────────────────────────────────
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

                    string contentHash;
                    if (pair is { BinaryExists: false, PointerHash: not null })
                    {
                        // Pointer-only: use pointer hash directly (no re-hash)
                        contentHash = pair.PointerHash;
                    }
                    else if (pair.BinaryExists)
                    {
                        await using var fs        = File.OpenRead(fullBinaryPath!);
                        var             hashBytes = await _encryption.ComputeHashAsync(fs, ct);
                        contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                    else
                    {
                        // No binary and no pointer hash → skip
                        _logger.LogWarning("Skipping FilePair with neither binary nor pointer hash: {Path}", pair.RelativePath);
                        return;
                    }

                    await _mediator.Publish(new FileHashedEvent(pair.RelativePath, contentHash), ct);

                    _logger.LogInformation("[hash] {Path} -> {Hash} ({Size})", pair.RelativePath, contentHash[..8], fileSize.Bytes().Humanize());

                    await hashedChannel.Writer.WriteAsync(new HashedFilePair(pair, contentHash, opts.RootDirectory), ct);
                })
                .ContinueWith(_ => hashedChannel.Writer.Complete(), CancellationToken.None);

            // ── Stage 3: Dedup (×1) + Router (task 8.5, 8.6) ─────────────────
            var dedupTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var hashed in hashedChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        var known = await _index.LookupAsync([hashed.ContentHash], cancellationToken);

                        // Check for pointer-only with missing chunk (task 8.5)
                        if (!hashed.FilePair.BinaryExists)
                        {
                            if (!known.ContainsKey(hashed.ContentHash) && !inFlightHashes.ContainsKey(hashed.ContentHash))
                            {
                                _logger.LogWarning("Pointer-only file references missing chunk, skipping: {Path}", hashed.FilePair.RelativePath);
                                continue;
                            }

                            // Known dedup: add to manifest only
                            _logger.LogInformation("[dedup] {Path} -> hit (pointer-only)", hashed.FilePair.RelativePath);
                            await WriteManifestEntry(hashed, opts.RootDirectory, manifestWriter, cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            continue;
                        }

                        if (known.ContainsKey(hashed.ContentHash) || inFlightHashes.ContainsKey(hashed.ContentHash))
                        {
                            // Already in index OR already queued in this run → dedup hit
                            _logger.LogInformation("[dedup] {Path} -> hit ({Hash})", hashed.FilePair.RelativePath, hashed.ContentHash[..8]);
                            await WriteManifestEntry(hashed, opts.RootDirectory, manifestWriter, cancellationToken);
                            Interlocked.Increment(ref filesDeduped);
                            if (!opts.NoPointers)
                                pendingPointers.Add((Path.Combine(opts.RootDirectory, hashed.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), hashed.ContentHash));
                        }
                        else
                        {
                            // Needs upload → mark in-flight, route by size
                            inFlightHashes.TryAdd(hashed.ContentHash, hashed.ContentHash);
                            var fileSize = hashed.FilePair.FileSize ?? 0;
                            Interlocked.Add(ref totalSize, fileSize);
                            var upload = new FileToUpload(hashed, fileSize);
                            var route  = fileSize >= opts.SmallFileThreshold ? "large" : "small";
                            _logger.LogInformation("[dedup] {Path} -> new/{Route} ({Hash}, {Size})",
                                hashed.FilePair.RelativePath, route, hashed.ContentHash[..8],
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
            var largeUploadTask = Parallel.ForEachAsync(
                largeChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = UploadWorkers, CancellationToken = cancellationToken },
                async (upload, ct) =>
                {
                    _logger.LogInformation("[upload] Start: {Path} ({Hash}, {Size})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash[..8], upload.FileSize.Bytes().Humanize());

                    await _mediator.Publish(new ChunkUploadingEvent(upload.HashedPair.ContentHash, upload.FileSize), ct);

                    var blobName = BlobPaths.Chunk(upload.HashedPair.ContentHash);

                    long compressedSize;

                    retry:
                    try
                    {
                        var fullPath    = Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        var contentType = _encryption.IsEncrypted ? ContentTypes.LargeEncrypted : ContentTypes.LargePlaintext;

                        // Streaming chain: ProgressStream(FileStream) → GZipStream → EncryptingStream → CountingStream → OpenWriteAsync
                        await using (var writeStream = await _blobs.OpenWriteAsync(blobName, contentType, ct))
                        {
                            var             countingStream = new CountingStream(writeStream);
                            await using var encStream      = _encryption.WrapForEncryption(countingStream);
                            await using var gzipStream     = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true);
                            await using (var fs = File.OpenRead(fullPath))
                            {
                                IProgress<long> noOpProgress = new Progress<long>();
                                await using var ps = new ProgressStream(fs, noOpProgress);
                                await ps.CopyToAsync(gzipStream, ct);
                            }

                            await gzipStream.DisposeAsync();
                            await encStream.DisposeAsync();
                            compressedSize = countingStream.BytesWritten;
                        } // writeStream disposed here → commits the upload

                        // Set metadata before tier: Archive-tier blobs are offline and reject SetMetadata.
                        var uploadMeta = new Dictionary<string, string>
                        {
                            [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
                            [BlobMetadataKeys.OriginalSize] = upload.FileSize.ToString(),
                            [BlobMetadataKeys.ChunkSize]    = compressedSize.ToString(),
                        };
                        await _blobs.SetMetadataAsync(blobName, uploadMeta, ct);
                        await _blobs.SetTierAsync(blobName, opts.UploadTier, ct);
                    }
                    catch (BlobAlreadyExistsException)
                    {
                        // Crash recovery: blob exists from a prior interrupted run.
                        var meta = await _blobs.GetMetadataAsync(blobName, ct);
                        if (meta.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                        {
                            // Upload + metadata both complete — recover compressed size and continue.
                            compressedSize = meta.ContentLength ?? 0;
                        }
                        else
                        {
                            // Upload committed but metadata not yet written — partial state, wipe and retry.
                            await _blobs.DeleteAsync(blobName, ct);
                            goto retry;
                        }
                    }

                    var entry = new IndexEntry(upload.HashedPair.ContentHash, upload.HashedPair.ContentHash, upload.FileSize, compressedSize);
                    _index.RecordEntry(new ShardEntry(entry.ContentHash, entry.ChunkHash, entry.OriginalSize, entry.CompressedSize));
                    await indexEntryChannel.Writer.WriteAsync(entry, ct);

                    await WriteManifestEntry(upload.HashedPair, opts.RootDirectory, manifestWriter, ct);
                    Interlocked.Increment(ref filesUploaded);

                    if (!opts.NoPointers)
                        pendingPointers.Add((Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), upload.HashedPair.ContentHash));

                    if (opts.RemoveLocal)
                        pendingDeletes.Add(Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                    await _mediator.Publish(new ChunkUploadedEvent(upload.HashedPair.ContentHash, compressedSize), ct);

                    _logger.LogInformation("[upload] Done: {Path} ({Hash}, orig={Orig}, compressed={Compressed})", upload.HashedPair.FilePair.RelativePath, upload.HashedPair.ContentHash[..8], upload.FileSize.Bytes().Humanize(), compressedSize.Bytes().Humanize());
                });

            // ── Stage 4b: Tar builder ×1 (task 8.8) ───────────────────────────
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
                        string tarHash;
                        await using (var fs = File.OpenRead(currentTarPath!))
                        {
                            var hashBytes = await _encryption.ComputeHashAsync(fs, cancellationToken);
                            tarHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                        }

                        await _mediator.Publish(new TarBundleSealingEvent(tarEntries.Count, currentSize), cancellationToken);

                        _logger.LogInformation("[tar] Sealed: {TarHash} {Count} file(s), {Size}", tarHash[..8], tarEntries.Count, currentSize.Bytes().Humanize());
                        foreach (var te in tarEntries)
                            _logger.LogInformation("[tar] Entry: {Path} ({Hash}, {Size})", te.HashedPair.FilePair.RelativePath, te.ContentHash[..8], te.OriginalSize.Bytes().Humanize());

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
                            tarStream = new FileStream(currentTarPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                            tarWriter = new TarWriter(tarStream, leaveOpen: false);
                        }

                        var fullPath = Path.Combine(opts.RootDirectory, upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        // Write entry named by content-hash (not original path)
                        var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, upload.HashedPair.ContentHash);
                        await using (var fs = File.OpenRead(fullPath))
                        {
                            tarEntry.DataStream = fs;
                            await tarWriter.WriteEntryAsync(tarEntry, cancellationToken);
                        }

                        tarEntry.DataStream = null;

                        tarEntries.Add(new TarEntry(upload.HashedPair.ContentHash, upload.FileSize, upload.HashedPair));
                        currentSize += upload.FileSize;

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
            var tarUploadTask = Parallel.ForEachAsync(
                sealedTarChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = UploadWorkers, CancellationToken = cancellationToken },
                async (sealed_, ct) =>
                {
                    try
                    {
                        await _mediator.Publish(new ChunkUploadingEvent(sealed_.TarHash, sealed_.UncompressedSize), ct);

                        var  blobName = BlobPaths.Chunk(sealed_.TarHash);
                        long compressedSize;

                        retry:
                        try
                        {
                            // Streaming chain: FileStream(tar) → GZipStream → EncryptingStream → CountingStream → OpenWriteAsync
                            var contentType = _encryption.IsEncrypted ? ContentTypes.TarEncrypted : ContentTypes.TarPlaintext;
                            await using (var writeStream = await _blobs.OpenWriteAsync(blobName, contentType, ct))
                            {
                                var             countingStream = new CountingStream(writeStream);
                                await using var encStream      = _encryption.WrapForEncryption(countingStream);
                                await using var gzipStream     = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true);
                                await using (var fs = File.OpenRead(sealed_.TarFilePath))
                                    await fs.CopyToAsync(gzipStream, ct);

                                await gzipStream.DisposeAsync();
                                await encStream.DisposeAsync();
                                compressedSize = countingStream.BytesWritten;
                            } // writeStream disposed here → commits the upload

                            // Set metadata before tier: Archive-tier blobs are offline and reject SetMetadata.
                            var uploadMeta = new Dictionary<string, string>
                            {
                                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar,
                                [BlobMetadataKeys.ChunkSize] = compressedSize.ToString(),
                            };
                            await _blobs.SetMetadataAsync(blobName, uploadMeta, ct);
                            await _blobs.SetTierAsync(blobName, opts.UploadTier, ct);
                        }
                        catch (BlobAlreadyExistsException)
                        {
                            // Crash recovery: blob exists from a prior interrupted run.
                            var meta = await _blobs.GetMetadataAsync(blobName, ct);
                            if (meta.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                            {
                                // Upload + metadata both complete — recover compressed size and continue.
                                compressedSize = meta.ContentLength ?? 0;
                            }
                            else
                            {
                                // Upload committed but metadata not yet written — partial state, wipe and retry.
                                await _blobs.DeleteAsync(blobName, ct);
                                goto retry;
                            }
                        }

                        var proportionalFactor = sealed_.UncompressedSize > 0
                            ? (double)compressedSize / sealed_.UncompressedSize
                            : 1.0;

                        // Create thin chunks for each entry
                        foreach (var entry in sealed_.Entries)
                        {
                            var thinBlobName = BlobPaths.Chunk(entry.ContentHash);
                            var proportional = (long)(entry.OriginalSize * proportionalFactor);
                            var thinMeta = new Dictionary<string, string>
                            {
                                [BlobMetadataKeys.AriusType]      = BlobMetadataKeys.TypeThin,
                                [BlobMetadataKeys.OriginalSize]   = entry.OriginalSize.ToString(),
                                [BlobMetadataKeys.CompressedSize] = proportional.ToString(),
                            };

                            retryThin:
                            try
                            {
                                await _blobs.UploadAsync(
                                    blobName: thinBlobName,
                                    content: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sealed_.TarHash)),
                                    metadata: new Dictionary<string, string>(),
                                    tier: BlobTier.Cool,
                                    contentType: ContentTypes.Thin,
                                    overwrite: false,
                                    cancellationToken: ct);
                                await _blobs.SetMetadataAsync(thinBlobName, thinMeta, ct);
                            }
                            catch (BlobAlreadyExistsException)
                            {
                                // Crash recovery: thin chunk exists from a prior interrupted run.
                                var existingMeta = await _blobs.GetMetadataAsync(thinBlobName, ct);
                                if (!existingMeta.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                                {
                                    // Upload committed but metadata not yet written — partial state, wipe and retry.
                                    await _blobs.DeleteAsync(thinBlobName, ct);
                                    goto retryThin;
                                }
                                // else: fully uploaded — nothing to do.
                            }

                            _index.RecordEntry(new ShardEntry(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional));
                            await indexEntryChannel.Writer.WriteAsync(new IndexEntry(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional), ct);

                            await WriteManifestEntry(entry.HashedPair, opts.RootDirectory, manifestWriter, ct);

                            if (!opts.NoPointers)
                                pendingPointers.Add((Path.Combine(opts.RootDirectory, entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)), entry.ContentHash));

                            if (opts.RemoveLocal)
                                pendingDeletes.Add(Path.Combine(opts.RootDirectory, entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        }

                        await _mediator.Publish(new TarBundleUploadedEvent(sealed_.TarHash, compressedSize, sealed_.Entries.Count), ct);
                        _logger.LogInformation("[tar] Uploaded: {TarHash} {Count} thin chunks, compressed={Compressed}", sealed_.TarHash[..8], sealed_.Entries.Count, compressedSize.Bytes().Humanize());
                        Interlocked.Add(ref filesUploaded, sealed_.Entries.Count);
                    }
                    finally
                    {
                        try { File.Delete(sealed_.TarFilePath); } catch { /* ignore */ }
                    }
                });

            // Wait for all upload stages to complete
            await largeUploadTask;
            await tarBuilderTask;
            await tarUploadTask;
            indexEntryChannel.Writer.Complete();
            await dedupTask;
            await enumTask;

            // ── End-of-pipeline ───────────────────────────────────────────────

            // Task 8.10: Index flush
            await _index.FlushAsync(cancellationToken);
            _logger.LogInformation("[index] Flush complete");

            // Task 8.11: Sort manifest → build tree → create snapshot
            await manifestWriter.DisposeAsync();
            await ManifestSorter.SortAsync(manifestPath, cancellationToken);

            var treeBuilder = new TreeBuilder(_blobs, _encryption, _accountName, _containerName);
            var rootHash    = await treeBuilder.BuildAsync(manifestPath, cancellationToken);
            _logger.LogInformation("[tree] Build complete: rootHash={RootHash}", rootHash is not null ? rootHash[..8] : "(none)");

            string?        snapshotRootHash = null;
            DateTimeOffset snapshotTime     = DateTimeOffset.UtcNow;

            if (rootHash is not null)
            {
                var snapshotSvc = new SnapshotService(_blobs, _encryption);
                var snapshot = await snapshotSvc.CreateAsync(rootHash, filesScanned, totalSize, cancellationToken: cancellationToken);
                snapshotRootHash = snapshot.RootHash;
                snapshotTime     = snapshot.Timestamp;
                _logger.LogInformation("[snapshot] Created: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash[..8]);

                await _mediator.Publish(new SnapshotCreatedEvent(rootHash, snapshot.Timestamp, snapshot.FileCount), cancellationToken);
            }

            // Task 8.12: Write pointer files ×N in parallel
            if (!opts.NoPointers)
            {
                await Parallel.ForEachAsync(pendingPointers, cancellationToken, async (item, ct) =>
                {
                    var (fullPath, hash) = item;
                    var pointerPath = fullPath + ".pointer.arius";
                    await File.WriteAllTextAsync(pointerPath, hash, ct);
                });
            }

            // Task 8.13: Remove local binary files
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
                RootHash      = null,
                SnapshotTime  = DateTimeOffset.UtcNow,
                ErrorMessage  = ex.Message
            };
        }
        finally
        {
            try
            {
                File.Delete(manifestPath);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WriteManifestEntry(HashedFilePair hashed, string rootDir, ManifestWriter writer, CancellationToken ct)
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
        var manifestPath = pair.RelativePath;
        if (!pair.BinaryExists && manifestPath.EndsWith(".pointer.arius", StringComparison.OrdinalIgnoreCase))
            manifestPath = manifestPath[..^".pointer.arius".Length];

        await writer.AppendAsync(new ManifestEntry(manifestPath, hashed.ContentHash, created, modified), ct);
    }
}