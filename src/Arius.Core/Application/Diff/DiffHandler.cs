using Arius.Core.Application.Abstractions;
using Arius.Core.Application.Ls;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Diff;

// ── Output ──────────────────────────────────────────────────────────────────

public enum DiffStatus { Added, Removed, Modified, TypeChanged }

public sealed record DiffEntry(
    DiffStatus   Status,
    string       Path,
    TreeNodeType? OldType = null,
    TreeNodeType? NewType = null,
    long?        OldSize = null,
    long?        NewSize = null,
    DateTimeOffset? OldMTime = null,
    DateTimeOffset? NewMTime = null);

// ── Request ──────────────────────────────────────────────────────────────────

public sealed record DiffRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    string SnapshotId1,
    string SnapshotId2) : IStreamRequest<DiffEntry>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class DiffHandler : IStreamRequestHandler<DiffRequest, DiffEntry>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public DiffHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<DiffEntry> Handle(
        DiffRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        var doc1 = await repo.LoadSnapshotDocumentAsync(request.SnapshotId1, cancellationToken);
        var doc2 = await repo.LoadSnapshotDocumentAsync(request.SnapshotId2, cancellationToken);

        // Flatten both snapshot trees into path→node dictionaries
        var left  = await FlattenTreeAsync(repo, doc1.Snapshot.Tree, cancellationToken);
        var right = await FlattenTreeAsync(repo, doc2.Snapshot.Tree, cancellationToken);

        // Removed
        foreach (var (path, node) in left.OrderBy(kv => kv.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!right.TryGetValue(path, out var rNode))
            {
                yield return new DiffEntry(DiffStatus.Removed, path,
                    OldType: node.Type, OldSize: node.Size, OldMTime: node.MTime);
            }
        }

        // Added + Modified + TypeChanged
        foreach (var (path, rNode) in right.OrderBy(kv => kv.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!left.TryGetValue(path, out var lNode))
            {
                yield return new DiffEntry(DiffStatus.Added, path,
                    NewType: rNode.Type, NewSize: rNode.Size, NewMTime: rNode.MTime);
            }
            else if (lNode.Type != rNode.Type)
            {
                yield return new DiffEntry(DiffStatus.TypeChanged, path,
                    OldType: lNode.Type, NewType: rNode.Type);
            }
            else if (lNode.ContentHashes.Count != rNode.ContentHashes.Count
                  || !lNode.ContentHashes.Select(h => h.Value)
                           .SequenceEqual(rNode.ContentHashes.Select(h => h.Value)))
            {
                yield return new DiffEntry(DiffStatus.Modified, path,
                    OldType: lNode.Type, NewType: rNode.Type,
                    OldSize: lNode.Size, NewSize: rNode.Size,
                    OldMTime: lNode.MTime, NewMTime: rNode.MTime);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, TreeNode>> FlattenTreeAsync(
        AzureRepository repo,
        TreeHash treeHash,
        CancellationToken ct,
        string prefix = "")
    {
        var result = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        var nodes  = await repo.ReadTreeAsync(treeHash, ct);

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var path = prefix + "/" + node.Name;
            result[path] = node;

            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } subHash)
            {
                var children = await FlattenTreeAsync(repo, subHash, ct, path);
                foreach (var (k, v) in children)
                    result[k] = v;
            }
        }

        return result;
    }
}
