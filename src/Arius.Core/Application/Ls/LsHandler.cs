using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Ls;

// ── Output model ────────────────────────────────────────────────────────────

/// <summary>
/// A single entry yielded from the <see cref="LsHandler"/>.
/// </summary>
public sealed record TreeEntry(
    string   Path,
    string   Name,
    TreeNodeType Type,
    long     Size,
    DateTimeOffset MTime,
    string   Mode);

// ── Request ─────────────────────────────────────────────────────────────────

/// <summary>
/// Request to list the contents of a snapshot at <paramref name="SnapshotId"/>
/// under the optional <paramref name="SubPath"/>. When <paramref name="Recursive"/>
/// is <see langword="true"/> all descendant entries are yielded.
/// </summary>
public sealed record LsRequest(
    string     ConnectionString,
    string     ContainerName,
    string     Passphrase,
    string     SnapshotId,
    string     SubPath   = "/",
    bool       Recursive = false) : IStreamRequest<TreeEntry>;

// ── Handler ──────────────────────────────────────────────────────────────────

public sealed class LsHandler : IStreamRequestHandler<LsRequest, TreeEntry>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public LsHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<TreeEntry> Handle(
        LsRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        var doc = await repo.LoadSnapshotDocumentAsync(request.SnapshotId, cancellationToken);

        // Normalise path: ensure leading + trailing slash
        var subPath = NormalisePath(request.SubPath);

        await foreach (var entry in WalkTreeAsync(
            repo, doc.Snapshot.Tree, subPath, request.Recursive, cancellationToken))
        {
            yield return entry;
        }
    }

    // ── Tree walk ─────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<TreeEntry> WalkTreeAsync(
        AzureRepository repo,
        TreeHash treeHash,
        string subPath,
        bool recursive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Walk from root, descending until we hit the target sub-path
        await foreach (var entry in WalkNodeAsync(repo, treeHash, "/", subPath, recursive, ct))
            yield return entry;
    }

    private static async IAsyncEnumerable<TreeEntry> WalkNodeAsync(
        AzureRepository repo,
        TreeHash treeHash,
        string currentPath,
        string targetPath,
        bool recursive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var nodes = await repo.ReadTreeAsync(treeHash, ct);

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var nodePath = currentPath.TrimEnd('/') + '/' + node.Name;

            // Are we inside the requested sub-tree?
            bool atOrBelowTarget = nodePath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase)
                                || targetPath.StartsWith(nodePath + '/', StringComparison.OrdinalIgnoreCase);

            if (!atOrBelowTarget)
                continue;

            // Yield this entry if it lives directly under the target path
            bool directChild = string.Equals(
                currentPath.TrimEnd('/') + '/', targetPath, StringComparison.OrdinalIgnoreCase);

            if (directChild)
            {
                yield return new TreeEntry(
                    Path: nodePath,
                    Name: node.Name,
                    Type: node.Type,
                    Size: node.Size,
                    MTime: node.MTime,
                    Mode: node.Mode);
            }

            // Recurse into directories
            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } subtreeHash)
            {
                bool shouldRecurse = recursive
                    || targetPath.StartsWith(nodePath + '/', StringComparison.OrdinalIgnoreCase);

                if (shouldRecurse)
                {
                    await foreach (var child in WalkNodeAsync(
                        repo, subtreeHash, nodePath + '/', targetPath, recursive, ct))
                        yield return child;
                }
            }
        }
    }

    private static string NormalisePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = '/' + path;
        if (!path.EndsWith('/'))  path = path + '/';
        return path;
    }
}
