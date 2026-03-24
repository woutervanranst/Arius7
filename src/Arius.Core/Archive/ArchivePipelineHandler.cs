using System.Collections.Concurrent;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Threading.Channels;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.LocalFile;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

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
public sealed class ArchivePipelineHandler
    : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    // ── Concurrency knobs ─────────────────────────────────────────────────────

    private const int HashWorkers      = 4;
    private const int UploadWorkers    = 4;
    private const int ChannelCapacity  = 64;
    private const int DedupBatchSize   = 512;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobStorageService              _blobs;
    private readonly IEncryptionService               _encryption;
    private readonly ChunkIndexService                _index;
    private readonly IMediator                        _mediator;
    private readonly ILogger<ArchivePipelineHandler>  _logger;
    private readonly string                           _accountName;
    private readonly string                           _containerName;

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

    // ── ICommandHandler ───────────────────────────────────────────────────────

    public async ValueTask<ArchiveResult> Handle(
        ArchiveCommand    command,
        CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // Validate options (task 8.13)
        if (opts.RemoveLocal && opts.NoPointers)
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
                    var pairs      = enumerator.Enumerate(opts.RootDirectory).ToList();
                    Interlocked.Add(ref filesScanned, pairs.Count);
                    await _mediator.Publish(new FileScannedEvent(pairs.Count), cancellationToken);

                    foreach (var pair in pairs)
                        await filePairChannel.Writer.WriteAsync(pair, cancellationToken);
                }
                finally
                {
                    filePairChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 2: Hash ×N (task 8.4) ───────────────────────────────────
            var hashTasks = Enumerable.Range(0, HashWorkers).Select(_ => Task.Run(async () =>
            {
                await foreach (var pair in filePairChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    await _mediator.Publish(new FileHashingEvent(pair.RelativePath), cancellationToken);

                    string contentHash;
                    if (!pair.BinaryExists && pair.PointerHash is not null)
                    {
                        // Pointer-only: use pointer hash directly (no re-hash)
                        contentHash = pair.PointerHash;
                    }
                    else if (pair.BinaryExists)
                    {
                        var fullPath = Path.Combine(opts.RootDirectory,
                            pair.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        await using var fs = File.OpenRead(fullPath);
                        var hashBytes = await _encryption.ComputeHashAsync(fs, cancellationToken);
                        contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                    else
                    {
                        // No binary and no pointer hash → skip
                        _logger.LogWarning("Skipping FilePair with neither binary nor pointer hash: {Path}",
                            pair.RelativePath);
                        continue;
                    }

                    await _mediator.Publish(new FileHashedEvent(pair.RelativePath, contentHash), cancellationToken);
                    await hashedChannel.Writer.WriteAsync(
                        new HashedFilePair(pair, contentHash, opts.RootDirectory), cancellationToken);
                }
            }, cancellationToken)).ToArray();

            var hashCompletion = Task.WhenAll(hashTasks).ContinueWith(t => hashedChannel.Writer.Complete(), CancellationToken.None);

            // ── Stage 3: Dedup (×1) + Router (task 8.5, 8.6) ─────────────────
            var dedupTask = Task.Run(async () =>
            {
                try
                {
                    var batch = new List<HashedFilePair>();

                    async Task FlushBatch()
                    {
                        if (batch.Count == 0) return;

                        var hashes  = batch.Select(p => p.ContentHash).Distinct().ToList();
                        var known   = await _index.LookupAsync(hashes, cancellationToken);

                        foreach (var hashed in batch)
                        {
                            // Check for pointer-only with missing chunk (task 8.5)
                            if (!hashed.FilePair.BinaryExists)
                            {
                                if (!known.ContainsKey(hashed.ContentHash) &&
                                    !inFlightHashes.ContainsKey(hashed.ContentHash))
                                {
                                    _logger.LogWarning(
                                        "Pointer-only file references missing chunk, skipping: {Path}",
                                        hashed.FilePair.RelativePath);
                                    continue;
                                }
                                // Known dedup: add to manifest only
                                await WriteManifestEntry(hashed, opts.RootDirectory, manifestWriter, cancellationToken);
                                Interlocked.Increment(ref filesDeduped);
                                continue;
                            }

                            if (known.TryGetValue(hashed.ContentHash, out _) ||
                                inFlightHashes.ContainsKey(hashed.ContentHash))
                            {
                                // Already in index OR already queued in this run → dedup hit
                                await WriteManifestEntry(hashed, opts.RootDirectory, manifestWriter, cancellationToken);
                                Interlocked.Increment(ref filesDeduped);
                                if (!opts.NoPointers)
                                    pendingPointers.Add((
                                        Path.Combine(opts.RootDirectory,
                                            hashed.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                                        hashed.ContentHash));
                            }
                            else
                            {
                                // Needs upload → mark in-flight, route by size
                                inFlightHashes.TryAdd(hashed.ContentHash, hashed.ContentHash);
                                var fileSize = hashed.FilePair.FileSize ?? 0;
                                Interlocked.Add(ref totalSize, fileSize);
                                var upload = new FileToUpload(hashed, fileSize);

                                if (fileSize >= opts.SmallFileThreshold)
                                    await largeChannel.Writer.WriteAsync(upload, cancellationToken);
                                else
                                    await smallChannel.Writer.WriteAsync(upload, cancellationToken);
                            }
                        }

                        batch.Clear();
                    }

                    await foreach (var hashed in hashedChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        batch.Add(hashed);
                        if (batch.Count >= DedupBatchSize)
                            await FlushBatch();
                    }

                    await FlushBatch(); // remaining
                }
                finally
                {
                    largeChannel.Writer.Complete();
                    smallChannel.Writer.Complete();
                }
            }, cancellationToken);

            // ── Stage 4a: Large file upload ×N (task 8.7) ─────────────────────
            var largeUploadTasks = Enumerable.Range(0, UploadWorkers).Select(_ => Task.Run(async () =>
            {
                await foreach (var upload in largeChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    await _mediator.Publish(
                        new ChunkUploadingEvent(upload.HashedPair.ContentHash, upload.FileSize), cancellationToken);

                    var blobName = BlobPaths.Chunk(upload.HashedPair.ContentHash);
                    var meta     = await _blobs.GetMetadataAsync(blobName, cancellationToken);

                    long compressedSize;

                    if (meta.Exists && meta.Metadata.TryGetValue(BlobMetadataKeys.AriusComplete, out var c) && c == "true")
                    {
                        // Crash recovery: already uploaded (task 9.1 / 9.3)
                        compressedSize = meta.ContentLength ?? 0;
                    }
                    else
                    {
                        var fullPath = Path.Combine(opts.RootDirectory,
                            upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        // Gzip + encrypt to memory in a single pass
                        MemoryStream uploadMs;
                        await using (var fs = File.OpenRead(fullPath))
                            uploadMs = await GzipEncryptToMemoryAsync(fs, _encryption, cancellationToken);

                        var uploadMeta = new Dictionary<string, string>
                        {
                            [BlobMetadataKeys.AriusType]     = BlobMetadataKeys.TypeLarge,
                            [BlobMetadataKeys.AriusComplete]  = "true",
                            [BlobMetadataKeys.OriginalSize]   = upload.FileSize.ToString(),
                            [BlobMetadataKeys.ChunkSize]      = uploadMs.Length.ToString(),
                        };

                        await _blobs.UploadAsync(blobName, uploadMs, uploadMeta,
                            opts.UploadTier,
                            _encryption.IsEncrypted ? ContentTypes.LargeEncrypted : ContentTypes.LargePlaintext,
                            overwrite: meta.Exists, cancellationToken: cancellationToken);
                        compressedSize = uploadMs.Length;
                    }

                    var entry = new IndexEntry(
                        upload.HashedPair.ContentHash, upload.HashedPair.ContentHash,
                        upload.FileSize, compressedSize);

                    _index.RecordEntry(new ShardEntry(entry.ContentHash, entry.ChunkHash,
                        entry.OriginalSize, entry.CompressedSize));
                    await indexEntryChannel.Writer.WriteAsync(entry, cancellationToken);

                    await WriteManifestEntry(upload.HashedPair, opts.RootDirectory, manifestWriter, cancellationToken);
                    Interlocked.Increment(ref filesUploaded);

                    if (!opts.NoPointers)
                        pendingPointers.Add((
                            Path.Combine(opts.RootDirectory,
                                upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                            upload.HashedPair.ContentHash));

                    if (opts.RemoveLocal)
                        pendingDeletes.Add(
                            Path.Combine(opts.RootDirectory,
                                upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                    await _mediator.Publish(
                        new ChunkUploadedEvent(upload.HashedPair.ContentHash, compressedSize), cancellationToken);
                }
            }, cancellationToken)).ToArray();

            // ── Stage 4b: Tar builder ×1 (task 8.8) ───────────────────────────
            var tarBuilderTask = Task.Run(async () =>
            {
                try
                {
                    var tarEntries       = new List<TarEntry>();
                    string? currentTarPath = null;
                    TarWriter? tarWriter   = null;
                    FileStream? tarStream  = null;
                    long currentSize       = 0;

                    async Task SealCurrentTar()
                    {
                        if (tarWriter is null) return;
                        await tarWriter.DisposeAsync();
                        tarStream!.Dispose();

                        // Compute tar hash
                        string tarHash;
                        await using (var fs = File.OpenRead(currentTarPath!))
                        {
                            var hashBytes = SHA256.HashData(fs);
                            tarHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                        }

                        await _mediator.Publish(
                            new TarBundleSealingEvent(tarEntries.Count, currentSize), cancellationToken);

                        await sealedTarChannel.Writer.WriteAsync(
                            new SealedTar(currentTarPath!, tarHash, currentSize, tarEntries.ToList()),
                            cancellationToken);

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
                            tarStream      = new FileStream(currentTarPath, FileMode.Create,
                                FileAccess.Write, FileShare.None, 65536, useAsync: true);
                            tarWriter = new TarWriter(tarStream, leaveOpen: false);
                        }

                        var fullPath = Path.Combine(opts.RootDirectory,
                            upload.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar));

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
            var tarUploadTasks = Enumerable.Range(0, UploadWorkers).Select(_ => Task.Run(async () =>
            {
                await foreach (var sealed_ in sealedTarChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        await _mediator.Publish(
                            new ChunkUploadingEvent(sealed_.TarHash, sealed_.UncompressedSize), cancellationToken);

                        var blobName = BlobPaths.Chunk(sealed_.TarHash);
                        var meta     = await _blobs.GetMetadataAsync(blobName, cancellationToken);
                        long compressedSize;

                        if (meta.Exists && meta.Metadata.TryGetValue(BlobMetadataKeys.AriusComplete, out var c) && c == "true")
                        {
                            compressedSize = meta.ContentLength ?? 0;
                        }
                        else
                        {
                            // Upload: tar → gzip → encrypt
                            MemoryStream uploadMs;
                            await using (var fs = File.OpenRead(sealed_.TarFilePath))
                                uploadMs = await GzipEncryptToMemoryAsync(fs, _encryption, cancellationToken);
                            compressedSize = uploadMs.Length;

                            var uploadMeta = new Dictionary<string, string>
                            {
                                [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeTar,
                                [BlobMetadataKeys.AriusComplete] = "true",
                                [BlobMetadataKeys.ChunkSize]     = compressedSize.ToString(),
                            };

                            await _blobs.UploadAsync(blobName, uploadMs, uploadMeta,
                                opts.UploadTier,
                                _encryption.IsEncrypted ? ContentTypes.TarEncrypted : ContentTypes.TarPlaintext,
                                overwrite: meta.Exists, cancellationToken: cancellationToken);
                        }

                        var proportionalFactor = sealed_.UncompressedSize > 0
                            ? (double)compressedSize / sealed_.UncompressedSize
                            : 1.0;

                        // Create thin chunks for each entry
                        foreach (var entry in sealed_.Entries)
                        {
                            var thinBlobName  = BlobPaths.Chunk(entry.ContentHash);
                            var proportional  = (long)(entry.OriginalSize * proportionalFactor);
                            var thinMeta = new Dictionary<string, string>
                            {
                                [BlobMetadataKeys.AriusType]      = BlobMetadataKeys.TypeThin,
                                [BlobMetadataKeys.AriusComplete]   = "true",
                                [BlobMetadataKeys.OriginalSize]    = entry.OriginalSize.ToString(),
                                [BlobMetadataKeys.CompressedSize]  = proportional.ToString(),
                            };

                            var thinMeta2 = await _blobs.GetMetadataAsync(thinBlobName, cancellationToken);
                            if (!thinMeta2.Exists || !thinMeta2.Metadata.TryGetValue(BlobMetadataKeys.AriusComplete, out string? _))
                            {
                                await _blobs.UploadAsync(
                                    thinBlobName,
                                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sealed_.TarHash)),
                                    thinMeta,
                                    BlobTier.Cool,
                                    ContentTypes.Thin,
                                    overwrite: thinMeta2.Exists,
                                    cancellationToken: cancellationToken);
                            }

                            _index.RecordEntry(new ShardEntry(entry.ContentHash, sealed_.TarHash,
                                entry.OriginalSize, proportional));
                            await indexEntryChannel.Writer.WriteAsync(
                                new IndexEntry(entry.ContentHash, sealed_.TarHash, entry.OriginalSize, proportional),
                                cancellationToken);

                            // Write manifest entry so the tree builder includes this file
                            await WriteManifestEntry(entry.HashedPair, opts.RootDirectory, manifestWriter, cancellationToken);

                            if (!opts.NoPointers)
                                pendingPointers.Add((
                                    Path.Combine(opts.RootDirectory,
                                        entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                                    entry.ContentHash));

                            if (opts.RemoveLocal)
                                pendingDeletes.Add(
                                    Path.Combine(opts.RootDirectory,
                                        entry.HashedPair.FilePair.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        }

                        await _mediator.Publish(
                            new TarBundleUploadedEvent(sealed_.TarHash, compressedSize, sealed_.Entries.Count),
                            cancellationToken);
                        Interlocked.Add(ref filesUploaded, sealed_.Entries.Count);
                    }
                    finally
                    {
                        // Clean up tar temp file
                        try { File.Delete(sealed_.TarFilePath); } catch { /* ignore */ }
                    }
                }
            }, cancellationToken)).ToArray();

            // Wait for all upload stages to complete
            await Task.WhenAll(largeUploadTasks);
            await tarBuilderTask;
            await Task.WhenAll(tarUploadTasks);
            indexEntryChannel.Writer.Complete();
            await dedupTask;
            await enumTask;

            // ── End-of-pipeline ───────────────────────────────────────────────

            // Task 8.10: Index flush
            await _index.FlushAsync(cancellationToken);

            // Task 8.11: Sort manifest → build tree → create snapshot
            await manifestWriter.DisposeAsync();
            await ManifestSorter.SortAsync(manifestPath, cancellationToken);

            var treeBuilder = new TreeBuilder(_blobs, _encryption, _accountName, _containerName);
            var rootHash    = await treeBuilder.BuildAsync(manifestPath, cancellationToken);

            string? snapshotRootHash = null;
            DateTimeOffset snapshotTime = DateTimeOffset.UtcNow;

            if (rootHash is not null)
            {
                var snapshotSvc = new SnapshotService(_blobs, _encryption);
                var snapshot    = await snapshotSvc.CreateAsync(
                    rootHash, filesScanned, totalSize, cancellationToken: cancellationToken);
                snapshotRootHash = snapshot.RootHash;
                snapshotTime     = snapshot.Timestamp;

                await _mediator.Publish(
                    new SnapshotCreatedEvent(rootHash, snapshot.Timestamp, snapshot.FileCount),
                    cancellationToken);
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
                    try { File.Delete(path); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete local file: {Path}", path); }
                }
            }

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
            try { File.Delete(manifestPath); } catch { /* ignore */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="source"/>, gzips it, encrypts it via <paramref name="encryption"/>,
    /// and returns the result as a rewound <see cref="MemoryStream"/>.
    ///
    /// Handles the plaintext case where <c>WrapForEncryption</c> returns the same stream
    /// instance (which must not be disposed prematurely).
    /// </summary>
    private static async Task<MemoryStream> GzipEncryptToMemoryAsync(
        Stream            source,
        IEncryptionService encryption,
        CancellationToken ct)
    {
        var ms         = new MemoryStream();
        var encWrapper = encryption.WrapForEncryption(ms);
        var isSameStream = ReferenceEquals(encWrapper, ms);

        await using (var gzip = new GZipStream(encWrapper, CompressionLevel.Optimal, leaveOpen: true))
            await source.CopyToAsync(gzip, ct);

        // Flush the encryption layer (if distinct) before rewinding
        if (!isSameStream)
            await encWrapper.FlushAsync(ct);

        // Only dispose the wrapper if it's a distinct stream (not ms itself)
        if (!isSameStream)
            await encWrapper.DisposeAsync();

        ms.Position = 0;
        return ms;
    }

    private static async Task WriteManifestEntry(
        HashedFilePair    hashed,
        string            rootDir,
        ManifestWriter    writer,
        CancellationToken ct)
    {
        var pair = hashed.FilePair;

        // Use binary metadata if available, otherwise pointer metadata
        DateTimeOffset created, modified;
        if (pair.BinaryExists)
        {
            var fullPath = Path.Combine(rootDir, pair.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var fi = new FileInfo(fullPath);
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

/// <summary>
/// A write-through stream wrapper that counts bytes written.
/// Used to measure compressed size without a double-pass.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly MemoryStream _inner = new();
    public long BytesWritten { get; private set; }

    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), ct);
        BytesWritten += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => _inner.Flush();
    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
    public override void SetLength(long value)                        => throw new NotSupportedException();
}
