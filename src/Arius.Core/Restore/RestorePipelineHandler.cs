using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
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
                            _logger.LogInformation("[conflict] {Path} -> skip (identical)", file.RelativePath);
                            skipped++;
                            await _mediator.Publish(new FileSkippedEvent(file.RelativePath), cancellationToken);
                            continue;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("[conflict] {Path} -> overwrite", file.RelativePath);
                    }
                }
                else
                {
                    _logger.LogInformation("[conflict] {Path} -> new", file.RelativePath);
                }

                toRestore.Add(file);
            }

            if (toRestore.Count == 0)
            {
                return new RestoreResult
                {
                    Success             = true,
                    FilesRestored       = 0,
                    FilesSkipped        = skipped,
                    ChunksPendingRehydration = 0,
                };
            }

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
            _logger.LogInformation("[chunk] Resolution: {Groups} chunk group(s), large={Large}, tar={Tar}", filesByChunkHash.Count, largeChunks, filesByChunkHash.Count - largeChunks);

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
                    rehydrated.Add(chunkHash);
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

            var costEstimate = new RehydrationCostEstimate
            {
                ChunksAvailable          = available.Count,
                ChunksAlreadyRehydrated  = rehydrated.Count,
                ChunksNeedingRehydration = needsRehydration.Count,
                ChunksPendingRehydration = rehydrationPending.Count,
                RehydrationBytes         = rehydrationBytes,
                DownloadBytes            = downloadBytes,
            };

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

            int filesRestored = 0;

            // Download both directly-available and already-rehydrated chunks
            foreach (var chunkHash in available.Concat(rehydrated))
            {
                var isRehydrated = rehydrated.Contains(chunkHash);
                var blobName = isRehydrated
                    ? BlobPaths.ChunkRehydrated(chunkHash)
                    : BlobPaths.Chunk(chunkHash);

                var filesForChunk = filesByChunkHash[chunkHash];

                // Determine chunk type from index entry
                // If content-hash == chunk-hash → large file
                // otherwise → thin/tar bundle
                var firstFile   = filesForChunk[0];
                if (!indexEntries.TryGetValue(firstFile.ContentHash, out var indexEntry))
                    continue;

                bool isLargeChunk = indexEntry.ContentHash == indexEntry.ChunkHash;

                _logger.LogInformation("[download] Chunk {ChunkHash} ({Type}, {FileCount} file(s), compressed={Compressed})", chunkHash[..8], isLargeChunk ? "large" : "tar", filesForChunk.Count, indexEntry.CompressedSize.Bytes().Humanize());

                if (isLargeChunk)
                {
                    // Large file: single file maps to this chunk
                    var file = filesForChunk[0]; // only one file per large chunk
                    await RestoreLargeFileAsync(blobName, file, opts, cancellationToken);
                    filesRestored++;
                    await _mediator.Publish(new FileRestoredEvent(file.RelativePath), cancellationToken);
                }
                else
                {
                    // Tar bundle: stream through tar, extract matching entries.
                    // Multiple files may share the same content hash (duplicates), so use a lookup.
                    var filesByContentHash = filesForChunk
                        .GroupBy(f => f.ContentHash, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                    var restored = await RestoreTarBundleAsync(
                        blobName, filesByContentHash, opts, cancellationToken);
                    filesRestored += restored;
                }
            }

            // ── Step 8: Phase 2 — kick off rehydration (tasks 10.8, 10.9) ────────

            // Task 10.9: also re-request chunks that are still pending from a previous run.
            var chunksToRequest = needsRehydration.Concat(rehydrationPending).ToList();
            int chunksToRehydrate = chunksToRequest.Count;

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

            int totalPending = chunksToRehydrate;

            // ── Step 9 (task 10.10): Cleanup rehydrated blobs after full restore ─

            if (totalPending == 0 && rehydrated.Count > 0)
            {
                // All chunks were downloaded; rehydrated copies can be cleaned up.
                long totalRehydratedBytes = 0;
                foreach (var chunkHash in rehydrated)
                {
                    if (indexEntries.TryGetValue(filesByChunkHash[chunkHash][0].ContentHash, out var ie))
                        totalRehydratedBytes += ie.CompressedSize;
                }

                if (opts.ConfirmCleanup is not null && await opts.ConfirmCleanup(rehydrated.Count, totalRehydratedBytes, cancellationToken))
                {
                    foreach (var chunkHash in rehydrated)
                    {
                        var blobName = BlobPaths.ChunkRehydrated(chunkHash);
                        try
                        {
                            await _blobs.DeleteAsync(blobName, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete rehydrated chunk {ChunkHash}", chunkHash);
                        }
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
    /// </summary>
    private async Task<List<FileToRestore>> CollectFilesAsync(string rootHash, string? targetPath, TreeBuilder treeCache, CancellationToken cancellationToken)
    {
        var result = new List<FileToRestore>();
        var prefix = NormalizePath(targetPath);

        await WalkTreeAsync(rootHash, string.Empty, prefix, result, cancellationToken);
        return result;
    }

    private async Task WalkTreeAsync(
        string             treeHash,
        string             currentPath,     // forward-slash relative, no trailing slash
        string?            targetPrefix,
        List<FileToRestore> result,
        CancellationToken  cancellationToken)
    {
        // Skip entire subtrees that cannot match the prefix filter
        if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix))
            return;

        // Load tree blob from cache/storage
        var blobName = BlobPaths.FileTree(treeHash);
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var treeBlob = await TreeBlobSerializer.DeserializeAsync(stream, cancellationToken);

        foreach (var entry in treeBlob.Entries)
        {
            var entryPath = currentPath.Length == 0
                ? entry.Name
                : $"{currentPath}/{entry.Name}";

            if (entry.Type == TreeEntryType.Dir)
            {
                // Strip trailing slash from directory name used in path assembly
                var dirPath = entryPath.TrimEnd('/');
                await WalkTreeAsync(entry.Hash, dirPath, targetPrefix, result, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(opts.RootDirectory,
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        // Streaming: download → decrypt → gunzip → write
        await using var downloadStream = await _blobs.DownloadAsync(blobName, cancellationToken);
        await using var decryptStream  = _encryption.WrapForDecryption(downloadStream);
        await using var gzipStream     = new GZipStream(decryptStream, CompressionMode.Decompress);
        await using var outputStream   = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        await gzipStream.CopyToAsync(outputStream, cancellationToken);

        // Set file timestamps from tree metadata
        File.SetCreationTimeUtc(localPath,  file.Created.UtcDateTime);
        File.SetLastWriteTimeUtc(localPath, file.Modified.UtcDateTime);

        // Create pointer file (task 10.11)
        if (!opts.NoPointers)
            await File.WriteAllTextAsync(localPath + ".pointer.arius", file.ContentHash, cancellationToken);
    }

    // ── Tar bundle restore (task 10.7) ────────────────────────────────────────

    /// <summary>
    /// Downloads a tar bundle and extracts only the files whose content-hash matches
    /// an entry in <paramref name="filesNeeded"/>.
    /// </summary>
    private async Task<int> RestoreTarBundleAsync(
        string                                    blobName,
        Dictionary<string, List<FileToRestore>>   filesNeeded,
        RestoreOptions                            opts,
        CancellationToken                         cancellationToken)
    {
        int restored = 0;

        await using var downloadStream = await _blobs.DownloadAsync(blobName, cancellationToken);
        await using var decryptStream  = _encryption.WrapForDecryption(downloadStream);
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
                    await using var _ = File.Create(localPath);
                }

                // Set timestamps
                File.SetCreationTimeUtc(localPath,  file.Created.UtcDateTime);
                File.SetLastWriteTimeUtc(localPath, file.Modified.UtcDateTime);

                // Create pointer file (task 10.11)
                if (!opts.NoPointers)
                    await File.WriteAllTextAsync(localPath + ".pointer.arius", contentHash, cancellationToken);

                await _mediator.Publish(new FileRestoredEvent(file.RelativePath), cancellationToken);
                restored++;
            }
        }

        return restored;
    }
}