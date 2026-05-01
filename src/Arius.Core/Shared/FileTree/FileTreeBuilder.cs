using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using System.Threading.Channels;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Synchronizes Merkle tree blobs bottom-up from a staged filetree root.
/// </summary>
public sealed class FileTreeBuilder
{
    private const int SiblingSubtreeWorkers = 1;
    private const int UploadWorkers = 1;
    private const int UploadChannelCapacity = 16;

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

        var uploadChannel = Channel.CreateBounded<(FileTreeHash Hash, IReadOnlyList<FileTreeEntry> Entries)>(UploadChannelCapacity);
        var uploadTask = Parallel.ForEachAsync(uploadChannel.Reader.ReadAllAsync(cancellationToken),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = UploadWorkers },
            async (node, ct) =>
            {
                await _fileTreeService.EnsureStoredAsync(node.Hash, node.Entries, ct);
            });

        try
        {
            var rootHash = await BuildDirectoryAsync(FileTreeStagingPaths.GetDirectoryId(string.Empty), cancellationToken);
            uploadChannel.Writer.TryComplete();
            await uploadTask;
            return rootHash;
        }
        catch (Exception ex)
        {
            uploadChannel.Writer.TryComplete(ex);
            try
            {
                await uploadTask;
            }
            catch
            {
                // Surface the original build failure.
            }

            throw;
        }


        async Task<FileTreeHash?> BuildDirectoryAsync(string directoryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var lines = await ReadNodeLinesAsync(directoryId, ct);
            var (fileEntries, stagedDirectoryEntries) = ReadNodeEntries(lines);
            if (fileEntries.Count == 0 && stagedDirectoryEntries.Count == 0)
                return null;

            var directoryEntries = new DirectoryEntry?[stagedDirectoryEntries.Count];
            await Parallel.ForEachAsync(
                stagedDirectoryEntries.Select((directoryEntry, index) => (directoryEntry, index)),
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = SiblingSubtreeWorkers },
                async (item, ct2) =>
                {
                    var childHash = await BuildDirectoryAsync(item.directoryEntry.DirectoryNameHash, ct2);
                    if (childHash is not null)
                    {
                        directoryEntries[item.index] = new DirectoryEntry
                        {
                            Name         = item.directoryEntry.Name,
                            FileTreeHash = childHash.Value
                        };
                    }
                });

            var fileTreeEntries = new List<FileTreeEntry>(fileEntries.Count + directoryEntries.Length);
            fileTreeEntries.AddRange(fileEntries);
            fileTreeEntries.AddRange(directoryEntries.Where(entry => entry is not null)!);

            if (fileTreeEntries.Count == 0)
                return null;

            fileTreeEntries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            var hash = FileTreeSerializer.ComputeHash(fileTreeEntries, _encryption);
            await uploadChannel.Writer.WriteAsync((hash, fileTreeEntries), ct);
            return hash;
        }

        async Task<string[]> ReadNodeLinesAsync(string directoryId, CancellationToken ct)
        {
            var path = FileTreeStagingPaths.GetNodePath(stagingRoot, directoryId);
            if (!File.Exists(path))
                return []; // empty directory

            return await File.ReadAllLinesAsync(path, ct);
        }

        static (List<FileEntry> FileEntries, List<StagedDirectoryEntry> DirectoryEntries) ReadNodeEntries(IEnumerable<string> lines)
        {
            var fileEntries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var directoryEntries = new Dictionary<string, StagedDirectoryEntry>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                switch (FileTreeSerializer.ParseStagedEntryLine(line))
                {
                    case FileEntry fileEntry:
                        if (!fileEntries.TryAdd(fileEntry.Name, fileEntry))
                            throw new InvalidOperationException($"Duplicate staged file entry '{fileEntry.Name}'.");
                        break;

                    case StagedDirectoryEntry stagedDirectoryEntry:
                        if (directoryEntries.TryGetValue(stagedDirectoryEntry.Name, out var existingDirectoryEntry))
                        {
                            // the file is append only, directories are added when a file is added so a directoryEntry can appear multiple times, but it should be the same line every time
                            if (!string.Equals(existingDirectoryEntry.DirectoryNameHash, stagedDirectoryEntry.DirectoryNameHash, StringComparison.Ordinal))
                                throw new InvalidOperationException($"Conflicting staged directory entry '{stagedDirectoryEntry.Name}'.");

                            break;
                        }

                        directoryEntries.Add(stagedDirectoryEntry.Name, stagedDirectoryEntry);
                        break;
                }
            }

            return ([.. fileEntries.Values], [.. directoryEntries.Values]);
        }
    }
}
