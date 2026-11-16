using System.Runtime.CompilerServices;
using Arius.Core.Shared.FileTree;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Breadth-first traversal over snapshot filetree nodes for restore.
/// </summary>
internal sealed class FileTreeWalker(IFileTreeService fileTreeService)
{
    /// <summary>
    /// Walks snapshot filetrees breadth-first and yields the files that are, or lie under, any of
    /// <paramref name="targetPaths"/>, or all files when <paramref name="targetPaths"/> is empty. Each file is
    /// yielded once, even when targets overlap. Throws after the walk when a target is not in the snapshot.
    /// <see cref="RelativePath.Root"/> is not a valid target; pass no targets to walk the whole snapshot.
    /// </summary>
    public async IAsyncEnumerable<FileToRestore> WalkFilesAsync(
        FileTreeHash                      rootHash,
        IReadOnlyCollection<RelativePath> targetPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var targets = targetPaths.ToHashSet();
        // Ancestors of a target are walked to reach it, but their own files are not selected by it.
        var targetAncestors = targets.SelectMany(AncestorsAndSelf).ToHashSet();
        // Filetrees never store empty directories, so a target found in the tree always selects at least one file.
        var unmatchedTargets = targets.ToHashSet();

        // Selected: the directory is, or lies under, a target, so every file in it is restored.
        var pending = new Queue<(FileTreeHash TreeHash, RelativePath Path, bool Selected)>();
        pending.Enqueue((rootHash, RelativePath.Root, targets.Count == 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (treeHash, currentPath, selected) = pending.Dequeue();

            var entries = await fileTreeService.ReadAsync(treeHash, cancellationToken).ConfigureAwait(false);

            foreach (var fileEntry in entries.OfType<FileEntry>())
            {
                var entryPath = currentPath / fileEntry.Name;
                if (!selected && !targets.Contains(entryPath))
                    continue;

                unmatchedTargets.Remove(entryPath);
                yield return new FileToRestore(entryPath, fileEntry.ContentHash, fileEntry.Created, fileEntry.Modified);
            }

            foreach (var directoryEntry in entries.OfType<DirectoryEntry>())
            {
                var childPath = currentPath / directoryEntry.Name;
                unmatchedTargets.Remove(childPath);

                var childSelected = selected || targets.Contains(childPath);
                if (childSelected || targetAncestors.Contains(childPath))
                    pending.Enqueue((directoryEntry.FileTreeHash, childPath, childSelected));
            }
        }

        // A target not in the snapshot is most likely a typo: fail before the cost prompt or any write.
        if (unmatchedTargets.Count > 0)
            throw new InvalidOperationException($"{unmatchedTargets.Count} target path(s) not found in the snapshot: {string.Join(", ", unmatchedTargets.Take(5))}.");

        static IEnumerable<RelativePath> AncestorsAndSelf(RelativePath path)
        {
            for (RelativePath? current = path; current is not null; current = current.Value.Parent)
                yield return current.Value;
        }
    }
}
