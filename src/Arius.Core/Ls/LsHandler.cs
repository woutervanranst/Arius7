using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Ls;

/// <summary>
/// Implements the ls command: list files in a snapshot with optional filters.
///
/// Tasks 11.1 – 11.7:
/// 1. Resolve snapshot (11.1, 11.2)
/// 2. Walk Merkle tree with optional prefix filter (11.2, 11.3)
/// 3. Apply filename substring filter (11.4)
/// 4. Look up file sizes from chunk index (11.5)
/// </summary>
public sealed class LsHandler
    : ICommandHandler<LsCommand, LsResult>
{
    private readonly IBlobStorageService   _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly ChunkIndexService     _index;
    private readonly ILogger<LsHandler>   _logger;
    private readonly string               _accountName;
    private readonly string               _containerName;

    public LsHandler(
        IBlobStorageService  blobs,
        IEncryptionService   encryption,
        ChunkIndexService    index,
        ILogger<LsHandler>  logger,
        string               accountName,
        string               containerName)
    {
        _blobs         = blobs;
        _encryption    = encryption;
        _index         = index;
        _logger        = logger;
        _accountName   = accountName;
        _containerName = containerName;
    }

    /// <summary>
    /// Lists files from a resolved snapshot according to the provided command options.
    /// </summary>
    /// <param name="command">The <see cref="LsCommand"/> containing Options that specify snapshot version, optional path prefix, and optional filename substring filter.</param>
    /// <returns>
    /// An <see cref="LsResult"/> describing the operation outcome. On success, <see cref="LsResult.Entries"/> contains matching files (each entry includes path, hash, size when available, created and modified timestamps) and <see cref="LsResult.Success"/> is true. On failure, <see cref="LsResult.Success"/> is false and <see cref="LsResult.ErrorMessage"/> contains an explanatory message (for example, when no snapshot is found or an error occurs).
    /// </returns>
    public async ValueTask<LsResult> Handle(
        LsCommand         command,
        CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // ── Operation start marker (task 5.3) ────────────────────────────────
        _logger.LogInformation(
            "[ls] Start: account={Account} container={Container} version={Version} prefix={Prefix} filter={Filter}",
            _accountName, _containerName, opts.Version ?? "latest", opts.Prefix ?? "(none)", opts.Filter ?? "(none)");

        try
        {
            // ── Resolve snapshot ─────────────────────────────────────────────

            var snapshotSvc = new SnapshotService(_blobs, _encryption);
            var snapshot    = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);

            if (snapshot is null)
            {
                return new LsResult
                {
                    Success      = false,
                    Entries      = Array.Empty<LsEntry>(),
                    ErrorMessage = opts.Version is null
                        ? "No snapshots found in this repository."
                        : $"Snapshot '{opts.Version}' not found."
                };
            }

            _logger.LogInformation("[snapshot] Resolved: {Timestamp} rootHash={RootHash}",
                snapshot.Timestamp.ToString("o"), snapshot.RootHash[..8]);

            // ── Collect file entries from tree ───────────────────────────────

            var prefix = NormalizePath(opts.Prefix);
            var rawEntries = new List<(string Path, string Hash, DateTimeOffset Created, DateTimeOffset Modified)>();
            await WalkTreeAsync(snapshot.RootHash, string.Empty, prefix, rawEntries, cancellationToken);

            _logger.LogInformation("[tree] Traversal complete: {Count} file(s) collected (prefix={Prefix})",
                rawEntries.Count, prefix ?? "(none)");

            // ── Apply filename substring filter (task 11.4) ───────────────────

            var filter = opts.Filter;
            if (filter is not null)
            {
                rawEntries = rawEntries
                    .Where(e =>
                    {
                        var name = Path.GetFileName(e.Path);
                        return name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
            }

            // ── Look up sizes from chunk index (task 11.5) ────────────────────

            var allHashes = rawEntries.Select(e => e.Hash).Distinct().ToList();
            var indexLookup = await _index.LookupAsync(allHashes, cancellationToken);

            // ── Build output entries ──────────────────────────────────────────

            var lsEntries = rawEntries
                .Select(e =>
                {
                    long? size = indexLookup.TryGetValue(e.Hash, out var ie) ? ie.OriginalSize : null;
                    return new LsEntry(e.Path, e.Hash, size, e.Created, e.Modified);
                })
                .ToList();

            _logger.LogInformation("[ls] Done: {Count} file(s) matched (prefix={Prefix} filter={Filter})",
                lsEntries.Count, prefix ?? "(none)", filter ?? "(none)");

            return new LsResult
            {
                Success = true,
                Entries = lsEntries,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ls command failed");
            return new LsResult
            {
                Success      = false,
                Entries      = Array.Empty<LsEntry>(),
                ErrorMessage = ex.Message
            };
        }
    }

    // ── Tree traversal (tasks 11.2, 11.3) ────────────────────────────────────

    private async Task WalkTreeAsync(
        string treeHash,
        string currentPath,
        string? targetPrefix,
        List<(string Path, string Hash, DateTimeOffset Created, DateTimeOffset Modified)> result,
        CancellationToken cancellationToken)
    {
        // Pruning: skip subtrees that cannot match the prefix
        if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix))
            return;

        var blobName = BlobPaths.FileTree(treeHash);
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption, cancellationToken);

        foreach (var entry in treeBlob.Entries)
        {
            var entryPath = currentPath.Length == 0
                ? entry.Name
                : $"{currentPath}/{entry.Name}";

            if (entry.Type == TreeEntryType.Dir)
            {
                var dirPath = entryPath.TrimEnd('/');
                await WalkTreeAsync(entry.Hash, dirPath, targetPrefix, result, cancellationToken);
            }
            else
            {
                // Prefix filter applied at file level
                if (targetPrefix is null || entryPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((
                        entryPath,
                        entry.Hash,
                        entry.Created  ?? DateTimeOffset.MinValue,
                        entry.Modified ?? DateTimeOffset.MinValue));
                }
            }
        }
    }

    private static bool IsPathRelevant(string currentPath, string targetPrefix)
    {
        if (currentPath.Length == 0) return true;
        return targetPrefix.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Replace('\\', '/').TrimEnd('/');
    }
}
