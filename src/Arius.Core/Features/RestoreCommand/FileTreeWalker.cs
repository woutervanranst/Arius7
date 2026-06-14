using System.Runtime.CompilerServices;
using Arius.Core.Shared.FileTree;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Breadth-first traversal over persisted remote filetree nodes for restore.
/// </summary>
internal sealed class FileTreeWalker(IFileTreeService fileTreeService)
{
    public async IAsyncEnumerable<FileToRestore> WalkFilesAsync(
        FileTreeHash rootHash,
        RelativePath? targetPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pending = new Queue<(FileTreeHash TreeHash, RelativePath Path)>();
        pending.Enqueue((rootHash, RelativePath.Root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (treeHash, currentPath) = pending.Dequeue();

            if (targetPrefix is not null && !IsPathRelevant(currentPath, targetPrefix.Value))
                continue;

            var entries = await fileTreeService.ReadAsync(treeHash, cancellationToken).ConfigureAwait(false);

            foreach (var fileEntry in entries.OfType<FileEntry>())
            {
                var entryPath = currentPath / fileEntry.Name;
                if (targetPrefix is not null && !entryPath.StartsWith(targetPrefix.Value))
                    continue;

                yield return new FileToRestore(entryPath, fileEntry.ContentHash, fileEntry.Created, fileEntry.Modified);
            }

            foreach (var directoryEntry in entries.OfType<DirectoryEntry>())
                pending.Enqueue((directoryEntry.FileTreeHash, currentPath / directoryEntry.Name));
        }
    }

    private static bool IsPathRelevant(RelativePath currentPath, RelativePath targetPrefix)
    {
        return currentPath == RelativePath.Root
            || targetPrefix.StartsWith(currentPath)
            || currentPath.StartsWith(targetPrefix);
    }
}
