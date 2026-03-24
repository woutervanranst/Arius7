using System.Formats.Tar;
using System.IO.Compression;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

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

    public async ValueTask<RestoreResult> Handle(
        RestoreCommand    command,
        CancellationToken cancellationToken)
    {
        var opts = command.Options;

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

            // ── Step 2: Tree traversal ────────────────────────────────────────

            var treeCache = new TreeBuilder(_blobs, _encryption, _accountName, _containerName);
            var files     = await CollectFilesAsync(snapshot.RootHash, opts.TargetPath, treeCache, cancellationToken);

            await _mediator.Publish(new RestoreStartedEvent(files.Count), cancellationToken);

            // ── Step 3: Conflict check ────────────────────────────────────────

            var toRestore = new List<FileToRestore>();
            int skipped   = 0;

            foreach (var file in files)
            {
                var localPath = Path.Combine(opts.RootDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

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
                            skipped++;
                            await _mediator.Publish(new FileSkippedEvent(file.RelativePath), cancellationToken);
                            continue;
                        }
                    }
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
                    _logger.LogWarning("Content hash not found in index, skipping: {Hash} ({Path})",
                        file.ContentHash, file.RelativePath);
                    unresolved.Add(file);
                    continue;
                }

                if (!filesByChunkHash.TryGetValue(entry.ChunkHash, out var list))
                    filesByChunkHash[entry.ChunkHash] = list = new List<FileToRestore>();
                list.Add(file);
            }

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

            // ── Step 8: Phase 2 — kick off rehydration ────────────────────────

            int chunksToRehydrate = needsRehydration.Count;
            if (chunksToRehydrate > 0)
            {
                long totalRehydrateBytes = 0;
                foreach (var chunkHash in needsRehydration)
                {
                    var chunkName = BlobPaths.Chunk(chunkHash);
                    var dst       = BlobPaths.ChunkRehydrated(chunkHash);
                    try
                    {
                        await _blobs.CopyAsync(chunkName, dst, BlobTier.Hot,
                            RehydratePriority.Standard, cancellationToken);

                        if (indexEntries.TryGetValue(filesByChunkHash[chunkHash][0].ContentHash, out var ie))
                            totalRehydrateBytes += ie.CompressedSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to start rehydration for chunk {ChunkHash}", chunkHash);
                    }
                }

                await _mediator.Publish(
                    new RehydrationStartedEvent(chunksToRehydrate, totalRehydrateBytes),
                    cancellationToken);
            }

            int totalPending = chunksToRehydrate + rehydrationPending.Count;

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
    private async Task<List<FileToRestore>> CollectFilesAsync(
        string      rootHash,
        string?     targetPath,
        TreeBuilder treeCache,
        CancellationToken cancellationToken)
    {
        var result   = new List<FileToRestore>();
        var prefix   = NormalizePath(targetPath);

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
        await using var outputStream   = new FileStream(
            localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

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
                var localPath = Path.Combine(opts.RootDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

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
