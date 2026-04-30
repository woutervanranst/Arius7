using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Synchronizes Merkle tree blobs bottom-up from a staged filetree root.
/// </summary>
public sealed class FileTreeBuilder
{
    private const int SiblingSubtreeWorkers = 4;

    private readonly IEncryptionService _encryption;
    private readonly FileTreeService _fileTreeService;

    /// <summary>
    /// Builds trees using shared services supplied by the caller/DI container.
    /// </summary>
    public FileTreeBuilder(
        IEncryptionService encryption,
        FileTreeService fileTreeService)
    {
        _encryption = encryption;
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

        return await BuildDirectoryAsync(FileTreeStagingPaths.GetDirectoryId(string.Empty), cancellationToken);


        async Task<FileTreeHash?> BuildDirectoryAsync(string directoryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var lines = await ReadNodeLinesAsync(directoryId, ct);
            var fileEntries = ReadFileEntries(lines);
            var directoryEntries = ReadDirectoryEntries(lines);
            if (fileEntries.Count == 0 && directoryEntries.Count == 0)
                return null;

            var childEntries = new DirectoryEntry?[directoryEntries.Count];
            await Parallel.ForEachAsync(
                directoryEntries.Select((directoryEntry, index) => (directoryEntry, index)),
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = SiblingSubtreeWorkers },
                async (item, ct) =>
                {
                    await BuildChildDirectoryAsync(item.directoryEntry, item.index, childEntries, ct);
                });

            var entries = new List<FileTreeEntry>(fileEntries.Count + childEntries.Length);
            entries.AddRange(fileEntries);
            entries.AddRange(childEntries.Where(entry => entry is not null)!);

            if (entries.Count == 0)
                return null;

            var hash = FileTreeSerializer.ComputeHash(entries, _encryption);
            return await _fileTreeService.EnsureStoredAsync(hash, entries, ct);
        }

        async Task<string[]> ReadNodeLinesAsync(string directoryId, CancellationToken ct)
        {
            var path = FileTreeStagingPaths.GetNodePath(stagingRoot, directoryId);
            if (!File.Exists(path))
                return [];

            return await File.ReadAllLinesAsync(path, ct);
        }

        static List<FileEntry> ReadFileEntries(IEnumerable<string> lines)
        {
            var entries = new List<FileEntry>();
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line) && FileTreeSerializer.ParseStagedEntryLine(line) is FileEntry entry)
                    entries.Add(entry);
            }

            return entries;
        }

        static List<StagedDirectoryEntry> ReadDirectoryEntries(IEnumerable<string> lines)
        {
            var links = new HashSet<StagedDirectoryEntry>();
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line) && FileTreeSerializer.ParseStagedEntryLine(line) is StagedDirectoryEntry entry)
                    links.Add(entry);
            }

            return links.OrderBy(link => link.Name, StringComparer.Ordinal).ToList();
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
