using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Extensions;
using Arius.Core.Shared.FileTree;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Streams the shared restore file pipeline: Walk -> Route -> Resolve.
/// Used by restore's classify and download passes so the handler can focus on orchestration.
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
    /// All files from the selected snapshot/target path that the restore command intends to bring back locally,
    /// subject to overwrite/skip rules, paired with the chunk-index entry needed to restore them.
    /// </summary>
    public async IAsyncEnumerable<ResolvedFile> StreamResolvedFilesAsync(
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
    /// Yields one <see cref="FileToRestore"/> per remote file that matches <paramref name="targetPrefix"/>
    /// (or all files when <c>null</c>) and emits restore-specific traversal progress.
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
