using System.Runtime.CompilerServices;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.SnapshotDiffQuery;

// --- QUERY

/// <summary>
/// Mediator stream query: report what changed between two snapshots. Walks both root filetrees in
/// lockstep, pruning subtrees whose <see cref="FileTreeHash"/> matches, and emits one classified
/// entry per changed path. <c>VersionA</c>/<c>VersionB</c> are <c>StartsWith</c>-matched the same way
/// as <c>ls --version</c> (see <see cref="ISnapshotService.ResolveAsync"/>).
/// </summary>
public sealed record SnapshotDiffQuery(string VersionA, string VersionB) : IStreamQuery<SnapshotDiffEntry>;

// --- RESULT

/// <summary>
/// How a single path changed between snapshot A and snapshot B. Exactly one value applies per path
/// (the diff is MECE) — deliberately NOT a <c>[Flags]</c> enum.
/// </summary>
public enum ChangeType
{
    /// <summary>Path present only in B.</summary>
    Added,
    /// <summary>Path present only in A.</summary>
    Removed,
    /// <summary>Same path, different <see cref="ContentHash"/>.</summary>
    Modified,
    /// <summary>Same path, same <see cref="ContentHash"/>, different Created/Modified.</summary>
    TimestampChanged,
}

/// <summary>
/// One changed path. <paramref name="Before"/> is the file entry in snapshot A, <paramref name="After"/>
/// in snapshot B: <see cref="ChangeType.Added"/> ⇒ Before is null; <see cref="ChangeType.Removed"/> ⇒
/// After is null; <see cref="ChangeType.Modified"/>/<see cref="ChangeType.TimestampChanged"/> ⇒ both set.
/// </summary>
public sealed record SnapshotDiffEntry(ChangeType Change, RelativePath Path, FileEntry? Before, FileEntry? After);

// --- HANDLER

/// <summary>
/// Resolves both manifests, warns on an Arius-version mismatch (the cross-platform line-ending hash
/// boundary), then BFS-walks the two root filetrees in lockstep. Equal child <see cref="FileTreeHash"/>
/// ⇒ prune; otherwise read both nodes and classify files by name. A subtree present on only one side
/// streams out wholesale as Added/Removed.
/// </summary>
public sealed class SnapshotDiffQueryHandler(
    ISnapshotService                  snapshots,
    IFileTreeService                  fileTree,
    ILogger<SnapshotDiffQueryHandler> logger)
    : IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>
{
    public async IAsyncEnumerable<SnapshotDiffEntry> Handle(
        SnapshotDiffQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifestA = await snapshots.ResolveAsync(query.VersionA, cancellationToken)
            ?? throw new InvalidOperationException($"Snapshot not found: '{query.VersionA}'.");
        var manifestB = await snapshots.ResolveAsync(query.VersionB, cancellationToken)
            ?? throw new InvalidOperationException($"Snapshot not found: '{query.VersionB}'.");

        if (!string.Equals(manifestA.AriusVersion, manifestB.AriusVersion, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "[diff] comparing snapshots written by different Arius versions ({VersionA} vs {VersionB}); " +
                "a cross-platform line-ending hash change can make identical content appear changed",
                manifestA.AriusVersion, manifestB.AriusVersion);
        }

        var queue = new Queue<DirectoryPair>();
        queue.Enqueue(new DirectoryPair(manifestA.RootHash, manifestB.RootHash, RelativePath.Root));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = queue.Dequeue();

            // Identical subtree → prune (no read, no allocation).
            if (pair.HashA is { } sameA && pair.HashB is { } sameB && sameA == sameB)
                continue;

            IReadOnlyList<FileTreeEntry> entriesA = pair.HashA is { } ha ? await fileTree.ReadAsync(ha, cancellationToken) : [];
            IReadOnlyList<FileTreeEntry> entriesB = pair.HashB is { } hb ? await fileTree.ReadAsync(hb, cancellationToken) : [];

            // ── Files: classify by name ──
            var filesA = entriesA.OfType<FileEntry>().ToDictionary(e => e.Name);
            var filesB = entriesB.OfType<FileEntry>().ToDictionary(e => e.Name);

            foreach (var (name, fileA) in filesA)
            {
                var path = pair.Path / name;
                if (filesB.TryGetValue(name, out var fileB))
                {
                    if (fileA.ContentHash != fileB.ContentHash)
                        yield return new SnapshotDiffEntry(ChangeType.Modified, path, fileA, fileB);
                    else if (fileA.Created != fileB.Created || fileA.Modified != fileB.Modified)
                        yield return new SnapshotDiffEntry(ChangeType.TimestampChanged, path, fileA, fileB);
                    // else identical → emit nothing
                }
                else
                {
                    yield return new SnapshotDiffEntry(ChangeType.Removed, path, fileA, null);
                }
            }

            foreach (var (name, fileB) in filesB)
            {
                if (!filesA.ContainsKey(name))
                    yield return new SnapshotDiffEntry(ChangeType.Added, pair.Path / name, null, fileB);
            }

            // ── Subdirectories: prune equal, enqueue differing/one-sided ──
            var dirsA = entriesA.OfType<DirectoryEntry>().ToDictionary(e => e.Name);
            var dirsB = entriesB.OfType<DirectoryEntry>().ToDictionary(e => e.Name);

            foreach (var (name, dirA) in dirsA)
            {
                var childPath = pair.Path / name;
                if (dirsB.TryGetValue(name, out var dirB))
                {
                    if (dirA.FileTreeHash != dirB.FileTreeHash)
                        queue.Enqueue(new DirectoryPair(dirA.FileTreeHash, dirB.FileTreeHash, childPath));
                    // equal → prune
                }
                else
                {
                    queue.Enqueue(new DirectoryPair(dirA.FileTreeHash, null, childPath)); // removed subtree
                }
            }

            foreach (var (name, dirB) in dirsB)
            {
                if (!dirsA.ContainsKey(name))
                    queue.Enqueue(new DirectoryPair(null, dirB.FileTreeHash, pair.Path / name)); // added subtree
            }
        }
    }

    private readonly record struct DirectoryPair(FileTreeHash? HashA, FileTreeHash? HashB, RelativePath Path);
}
