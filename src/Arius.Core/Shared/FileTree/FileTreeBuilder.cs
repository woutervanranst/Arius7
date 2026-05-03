using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Synchronizes Merkle tree blobs bottom-up from a staged filetree root.
/// </summary>
public sealed class FileTreeBuilder
{
    private const int SiblingSubtreeWorkers = 4;
    private const int UploadWorkers = 8;
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

    public static FileTreeHash ComputeHash(IReadOnlyList<FileTreeEntry> entries, IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(encryption);

        var plaintext = FileTreeSerializer.Serialize(entries);
        return FileTreeHash.Parse(encryption.ComputeHash(plaintext));
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Synchronizes the full Merkle tree from a staged filetree root, uploading any missing
    /// filetree blobs and returning the root tree hash. Returns <c>null</c> if the staging root
    /// contains no file entries.
    /// </summary>
    public async Task<FileTreeHash?> SynchronizeAsync(string stagingRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerCancellationToken = linkedCts.Token;
        ExceptionDispatchInfo? uploadFailure = null;

        var uploadChannel = Channel.CreateBounded<(FileTreeHash Hash, ReadOnlyMemory<byte> Plaintext)>(UploadChannelCapacity);
        var uploadTask = Parallel.ForEachAsync(uploadChannel.Reader.ReadAllAsync(workerCancellationToken),
            new ParallelOptions { CancellationToken = workerCancellationToken, MaxDegreeOfParallelism = UploadWorkers },
            async (node, ct) =>
            {
                try
                {
                    await _fileTreeService.EnsureStoredAsync(node, ct);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref uploadFailure, ExceptionDispatchInfo.Capture(ex), null);
                    linkedCts.Cancel();
                    throw;
                }
            });

        try
        {
            // Recursively build the FileTree starting from the root
            var rootPath = string.Empty;
            var rootHash = await BuildDirectoryAsync(FileTreePaths.GetStagingDirectoryId(rootPath), workerCancellationToken);
            uploadChannel.Writer.TryComplete();
            await uploadTask;
            return rootHash;
        }
        catch (OperationCanceledException) when (uploadFailure is not null && !cancellationToken.IsCancellationRequested)
        {
            await ObserveUploadTaskAsync(uploadFailure.SourceException);

            uploadFailure.Throw();
            throw new InvalidOperationException("Upload failure should have been rethrown.");
        }
        catch (Exception ex)
        {
            await ObserveUploadTaskAsync(ex);

            throw;
        }


        async Task<FileTreeHash?> BuildDirectoryAsync(string directoryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var lines = await ReadNodeLinesAsync(directoryId, ct);
            var (fileEntries, stagedDirectoryEntries) = ReadNodeEntries(lines);
            if (fileEntries.Empty() && stagedDirectoryEntries.Empty())
                return null;

            // Convert StagedDirectoryEntries to DirectoryEntries recursively (depth-first)
            var directoryEntries = new DirectoryEntry?[stagedDirectoryEntries.Length];
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

            var fileTreeEntries = fileEntries
                .Concat(directoryEntries.OfType<FileTreeEntry>())
                .ToArray(); // note: Serialize is doing the sorting

            if (fileTreeEntries.Length == 0)
                return null;

            var plaintext = FileTreeSerializer.Serialize(fileTreeEntries);
            var hash = FileTreeHash.Parse(_encryption.ComputeHash(plaintext));
            await uploadChannel.Writer.WriteAsync((hash, plaintext), ct);
            return hash;
        }

        async Task<string[]> ReadNodeLinesAsync(string directoryId, CancellationToken ct)
        {
            var path = FileTreePaths.GetStagingNodePath(stagingRoot, directoryId);
            if (!File.Exists(path))
                return []; // empty directory

            return await File.ReadAllLinesAsync(path, ct);
        }

        static (FileEntry[] FileEntries, StagedDirectoryEntry[] DirectoryEntries) ReadNodeEntries(IEnumerable<string> lines)
        {
            var fileEntries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var directoryEntries = new Dictionary<string, StagedDirectoryEntry>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                switch (FileTreeSerializer.ParseStagedNodeEntryLine(line))
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

        async Task ObserveUploadTaskAsync(Exception primaryException)
        {
            uploadChannel.Writer.TryComplete(primaryException);

            try
            {
                await uploadTask;
            }
            catch (OperationCanceledException) when (uploadFailure is not null && !cancellationToken.IsCancellationRequested)
            {
                primaryException.Data["SuppressedUploadCancellation"] = true;
            }
            catch (Exception uploadException) when (!ReferenceEquals(uploadException, primaryException))
            {
                // Preserve the original build failure while retaining the upload failure for diagnosis.
                primaryException.Data["FileTreeUploadFailure"] = uploadException;
            }
        }
    }
}
