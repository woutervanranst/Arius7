using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Find;

// ── Output model ─────────────────────────────────────────────────────────────

/// <summary>A single search result from <see cref="FindHandler"/>.</summary>
public sealed record SearchResult(
    string       SnapshotId,
    DateTimeOffset SnapshotTime,
    string       Path,
    string       Name,
    TreeNodeType Type,
    long         Size,
    DateTimeOffset MTime,
    string       Mode);

// ── Request ──────────────────────────────────────────────────────────────────

/// <summary>
/// Searches for files matching <paramref name="Pattern"/> (glob-style: * and ?)
/// across all snapshots, or a single snapshot if <paramref name="SnapshotId"/> is set.
/// </summary>
public sealed record FindRequest(
    string  ConnectionString,
    string  ContainerName,
    string  Passphrase,
    string  Pattern,
    string? SnapshotId = null,
    string? PathPrefix = null) : IStreamRequest<SearchResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class FindHandler : IStreamRequestHandler<FindRequest, SearchResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public FindHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<SearchResult> Handle(
        FindRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
        {
            // If caller wants only a specific snapshot, skip others
            if (request.SnapshotId is not null
                && !doc.Snapshot.Id.Value.StartsWith(request.SnapshotId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await foreach (var result in SearchSnapshotAsync(
                repo, doc.Snapshot, request.Pattern, request.PathPrefix, cancellationToken))
            {
                yield return result;
            }
        }
    }

    // ── Snapshot walk ────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<SearchResult> SearchSnapshotAsync(
        AzureRepository repo,
        Snapshot snapshot,
        string pattern,
        string? pathPrefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in WalkTreeAsync(repo, snapshot.Tree, "/", ct))
        {
            ct.ThrowIfCancellationRequested();

            if (pathPrefix is not null
                && !entry.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!GlobMatch(pattern, entry.Name))
                continue;

            yield return new SearchResult(
                SnapshotId:   snapshot.Id.Value,
                SnapshotTime: snapshot.Time,
                Path:         entry.Path,
                Name:         entry.Name,
                Type:         entry.Type,
                Size:         entry.Size,
                MTime:        entry.MTime,
                Mode:         entry.Mode);
        }
    }

    private static async IAsyncEnumerable<(string Path, string Name, TreeNodeType Type, long Size, DateTimeOffset MTime, string Mode)> WalkTreeAsync(
        AzureRepository repo,
        TreeHash treeHash,
        string currentPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var nodes = await repo.ReadTreeAsync(treeHash, ct);

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var nodePath = currentPath.TrimEnd('/') + '/' + node.Name;

            yield return (nodePath, node.Name, node.Type, node.Size, node.MTime, node.Mode);

            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } subtreeHash)
            {
                await foreach (var child in WalkTreeAsync(repo, subtreeHash, nodePath + '/', ct))
                    yield return child;
            }
        }
    }

    // ── Glob matching (supports * and ?) ────────────────────────────────────

    private static bool GlobMatch(string pattern, string input)
    {
        // Delegate to recursive match
        return GlobMatchSpan(pattern.AsSpan(), input.AsSpan());
    }

    private static bool GlobMatchSpan(ReadOnlySpan<char> pattern, ReadOnlySpan<char> input)
    {
        while (true)
        {
            if (pattern.IsEmpty)
                return input.IsEmpty;

            if (pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.IsEmpty) return true;

                for (int i = 0; i <= input.Length; i++)
                {
                    if (GlobMatchSpan(pattern, input[i..]))
                        return true;
                }
                return false;
            }

            if (input.IsEmpty)
                return false;

            if (pattern[0] != '?' && !char.Equals(
                char.ToLowerInvariant(pattern[0]),
                char.ToLowerInvariant(input[0])))
                return false;

            pattern = pattern[1..];
            input   = input[1..];
        }
    }
}
