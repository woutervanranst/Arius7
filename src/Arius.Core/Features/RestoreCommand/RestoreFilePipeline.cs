using System.Runtime.CompilerServices;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Extensions;
using Arius.Core.Shared.FileTree;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Streams the selected files that restore should write by composing Walk -> Route -> Resolve.
/// The classification pass uses this stream to count chunks and classify availability; the download pass uses it to group
/// available chunks without buffering the full file list.
/// </summary>
internal sealed class RestoreFilePipeline(
    IEncryptionService encryption,
    IChunkIndexService index,
    IFileTreeService fileTreeService,
    IMediator mediator,
    ILogger logger)
{
    private const int RouteWorkers     = 8;
    private const int ResolveBatchSize = 32;

    /// <summary>
    /// Streams selected snapshot files that should be restored locally, after applying overwrite/skip rules,
    /// paired with the chunk-index entry required to read their content.
    /// </summary>
    public async IAsyncEnumerable<ResolvedFile> GetStreamAsync(
        RelativeFileSystem fs,
        FileTreeHash       rootHash,
        RestoreOptions     opts,
        bool               emitEvents,
        StrongBox<long>?   skipped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var files = WalkAsync(rootHash, opts.TargetPath, emitProgress: emitEvents, cancellationToken)
            .WhereParallelAsync(
                RouteWorkers,
                (file, ct) => ShouldRestoreAsync(file, fs, opts, emitEvents, skipped, ct),
                cancellationToken);

        await foreach (var batch in files.Chunk(ResolveBatchSize).WithCancellation(cancellationToken))
            await foreach (var resolved in ResolveBatchAsync(batch, cancellationToken))
                yield return resolved;
    }

    /// <summary>
    /// Walks the snapshot filetree breadth-first and yields files under <paramref name="targetPrefix"/>,
    /// or every file when no target path is supplied. Optionally emits traversal progress.
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

        var walker = new FileTreeWalker(fileTreeService);
        await foreach (var file in walker.WalkFilesAsync(rootHash, targetPrefix, cancellationToken).ConfigureAwait(false))
        {
            yield return file;
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

        if (emitProgress && total > 0)
            await mediator.Publish(new TreeTraversalProgressEvent(total), cancellationToken);
    }

    /// <summary>
    /// Applies restore conflict rules for one file and returns whether it should continue to Resolve.
    /// Existing identical files and locally-different files without overwrite are skipped; missing files
    /// and overwrite-enabled files continue. Route events are emitted only during the classification pass.
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
                    if (skipped is not null)
                        Interlocked.Increment(ref skipped.Value);

                    if (emitEvents)
                    {
                        logger.LogInformation("[route] {Path} -> skip (identical)", file.RelativePath);
                        await mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length), cancellationToken);
                        await mediator.Publish(new FileRoutedEvent(file.RelativePath, RestoreRoute.SkipIdentical, s.Length), cancellationToken);
                    }

                    return false;
                }

                // Exists with a different hash and no --overwrite -> keep the local copy.
                if (skipped is not null)
                    Interlocked.Increment(ref skipped.Value);

                if (emitEvents)
                {
                    logger.LogInformation("[route] {Path} -> keep (local differs, no --overwrite)", file.RelativePath);
                    await mediator.Publish(new FileSkippedEvent(file.RelativePath, s.Length),                               cancellationToken);
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

    private async IAsyncEnumerable<ResolvedFile> ResolveBatchAsync(
        IReadOnlyList<FileToRestore> batch,
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
}
