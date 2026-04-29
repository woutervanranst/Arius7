using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Synchronizes Merkle tree blobs bottom-up from a staged filetree root.
/// </summary>
public sealed class FileTreeBuilder
{
    private const int SiblingSubtreeWorkers = 4;

    private readonly FileTreeService _fileTreeService;

    /// <summary>
    /// Builds trees using shared services supplied by the caller/DI container.
    /// </summary>
    public FileTreeBuilder(
        IEncryptionService encryption,
        FileTreeService fileTreeService)
    {
        _fileTreeService = fileTreeService;
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Synchronizes the full Merkle tree from a staged filetree root, uploading any missing
    /// filetree blobs and returning the root tree hash. Returns <c>null</c> if the staging root
    /// contains no file entries.
    /// </summary>
    public async Task<FileTreeHash?> SynchronizeAsync(
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);

        var subtreeWorkerBudget = new SemaphoreSlim(Math.Max(SiblingSubtreeWorkers - 1, 0));

        return await BuildDirectoryAsync(FileTreeStagingPaths.GetDirectoryId(string.Empty), cancellationToken);

        async Task<FileTreeHash?> BuildDirectoryAsync(string directoryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var fileEntriesTask = ReadFileEntriesAsync(directoryId, ct);
            var directoryEntriesTask = ReadDirectoryEntriesAsync(directoryId, ct);
            await Task.WhenAll(fileEntriesTask, directoryEntriesTask);

            var fileEntries = await fileEntriesTask;
            var directoryEntries = await directoryEntriesTask;
            if (fileEntries.Count == 0 && directoryEntries.Count == 0)
                return null;

            var childEntries = new DirectoryEntry?[directoryEntries.Count];
            var childTasks = new List<Task>(directoryEntries.Count);
            for (var index = 0; index < directoryEntries.Count; index++)
            {
                var directoryEntry = directoryEntries[index];
                if (await subtreeWorkerBudget.WaitAsync(0, ct))
                {
                    childTasks.Add(BuildChildDirectoryWithLeaseAsync(directoryEntry, index, childEntries, ct));
                    continue;
                }

                await BuildChildDirectoryAsync(directoryEntry, index, childEntries, ct);
            }

            await Task.WhenAll(childTasks);

            var entries = new List<FileTreeEntry>(fileEntries.Count + childEntries.Length);
            entries.AddRange(fileEntries);
            entries.AddRange(childEntries.Where(entry => entry is not null)!);

            if (entries.Count == 0)
                return null;

            var tree = new FileTreeBlob { Entries = entries };
            return await _fileTreeService.EnsureStoredAsync(tree, ct);
        }

        async Task<List<FileEntry>> ReadFileEntriesAsync(string directoryId, CancellationToken ct)
        {
            var path = FileTreeStagingPaths.GetEntriesPath(stagingRoot, directoryId);
            if (!File.Exists(path))
                return [];

            var lines = await File.ReadAllLinesAsync(path, ct);
            var entries = new List<FileEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    entries.Add(FileTreeBlobSerializer.ParseFileEntryLine(line));
            }

            return entries;
        }

        async Task<List<StagedDirectoryEntry>> ReadDirectoryEntriesAsync(string directoryId, CancellationToken ct)
        {
            var path = FileTreeStagingPaths.GetDirectoriesPath(stagingRoot, directoryId);
            if (!File.Exists(path))
                return [];

            var lines = await File.ReadAllLinesAsync(path, ct);
            var links = new HashSet<StagedDirectoryEntry>();
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    links.Add(StagedDirectoryEntry.Parse(line));
            }

            return links.OrderBy(link => link.Name, StringComparer.Ordinal).ToList();
        }

        async Task BuildChildDirectoryWithLeaseAsync(StagedDirectoryEntry directoryEntry, int index, DirectoryEntry?[] childEntries, CancellationToken ct)
        {
            try
            {
                await BuildChildDirectoryAsync(directoryEntry, index, childEntries, ct);
            }
            finally
            {
                subtreeWorkerBudget.Release();
            }
        }

        async Task BuildChildDirectoryAsync(StagedDirectoryEntry directoryEntry, int index, DirectoryEntry?[] childEntries, CancellationToken ct)
        {
            var childHash = await BuildDirectoryAsync(directoryEntry.DirectoryId, ct);
            if (childHash is not null)
            {
                childEntries[index] = new DirectoryEntry
                {
                    Name = directoryEntry.Name,
                    FileTreeHash = childHash.Value
                };
            }
        }
    }
}
