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
    /// yielded once, even when targets overlap. Throws after the walk when a target selected no file.
    /// </summary>
    public async IAsyncEnumerable<FileToRestore> WalkFilesAsync(
        FileTreeHash                      rootHash,
        IReadOnlyCollection<RelativePath> targetPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var targets = targetPaths.ToHashSet();
        // Ancestors of a target are walked to reach it, but their own files are not selected by it.
        var targetAncestors = targets.SelectMany(AncestorsAndSelf).ToHashSet();
        var matchedTargets  = new HashSet<RelativePath>();

        // Selected: the directory is, or lies under, a target, so every file in it is restored.
        var pending = new Queue<(FileTreeHash TreeHash, RelativePath Path, bool Selected)>();
        pending.Enqueue((rootHash, RelativePath.Root, targets.Count == 0 || targets.Contains(RelativePath.Root)));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (treeHash, currentPath, selected) = pending.Dequeue();

            var entries = await fileTreeService.ReadAsync(treeHash, cancellationToken).ConfigureAwait(false);

            var directoryMatchesRecorded = false;
            foreach (var fileEntry in entries.OfType<FileEntry>())
            {
                var entryPath = currentPath / fileEntry.Name;
                var isTarget  = targets.Contains(entryPath);
                if (!selected && !isTarget)
                    continue;

                if (isTarget)
                    matchedTargets.Add(entryPath);

                // All files in a selected directory match the same targets (those at or above it); record them once.
                if (selected && !directoryMatchesRecorded && targets.Count > 0)
                {
                    directoryMatchesRecorded = true;
                    matchedTargets.UnionWith(AncestorsAndSelf(currentPath).Where(targets.Contains));
                }

                yield return new FileToRestore(entryPath, fileEntry.ContentHash, fileEntry.Created, fileEntry.Modified);
            }

            foreach (var directoryEntry in entries.OfType<DirectoryEntry>())
            {
                var childPath     = currentPath / directoryEntry.Name;
                var childSelected = selected || targets.Contains(childPath);
                if (childSelected || targetAncestors.Contains(childPath))
                    pending.Enqueue((directoryEntry.FileTreeHash, childPath, childSelected));
            }
        }

        // A target that selects nothing is most likely a typo: fail before the cost prompt or any write.
        var unmatchedTargets = targets.Where(target => !matchedTargets.Contains(target)).ToArray();
        if (unmatchedTargets.Length > 0)
            throw new InvalidOperationException($"{unmatchedTargets.Length} target path(s) not found in the snapshot: {string.Join(", ", unmatchedTargets.Take(5))}.");

        static IEnumerable<RelativePath> AncestorsAndSelf(RelativePath path)
        {
            for (RelativePath? current = path; current is not null; current = current.Value.Parent)
                yield return current.Value;
        }
    }
}
