using System.Threading.Channels;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Synchronizes Merkle tree blobs bottom-up from a sorted manifest file.
///
/// Algorithm (tasks 5.6, 5.7, 5.8, 5.9):
/// 1. Stream sorted entries. Group by immediate parent directory.
/// 2. For each directory (deepest-first, bottom-up):
///    a. Collect file entries + already-computed child directory entries.
///    b. Build <see cref="FileTreeBlob"/>.
///    c. Compute tree hash.
///    d. Check via <see cref="FileTreeService.ExistsInRemote"/> — if present, skip upload (dedup).
///    e. Upload new blob via <see cref="FileTreeService.WriteAsync"/>; disk cache written automatically.
///    f. Cascade the directory hash up to the parent directory.
/// 3. Return root tree hash.
///
/// Empty directories are skipped (task 5.8): if no files reach a directory
/// (directly or recursively), that directory generates no tree blob.
/// </summary>
public sealed class FileTreeBuilder
{
    private const int FileTreeUploadWorkers = 4;

    private readonly IEncryptionService  _encryption;
    private readonly FileTreeService    _fileTreeService;

    /// <summary>
    /// Builds trees using shared services supplied by the caller/DI container.
    /// </summary>
    public FileTreeBuilder(
        IEncryptionService encryption,
        FileTreeService   fileTreeService)
    {
        _encryption = encryption;
        _fileTreeService  = fileTreeService;
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Synchronizes the full Merkle tree from a sorted manifest file, uploading any missing
    /// filetree blobs and returning the root tree hash. Returns <c>null</c> if the manifest is
    /// empty (nothing to archive).
    /// </summary>
    public async Task<FileTreeHash?> SynchronizeAsync(
        string            sortedManifestPath,
        CancellationToken cancellationToken = default)
    {
        var pendingUploads = Channel.CreateBounded<(FileTreeHash Hash, FileTreeBlob Tree)>(FileTreeUploadWorkers * 2);
        var uploadTasks = Enumerable.Range(0, FileTreeUploadWorkers)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var upload in pendingUploads.Reader.ReadAllAsync(cancellationToken))
                    await _fileTreeService.WriteAsync(upload.Hash, upload.Tree, cancellationToken);
            }, cancellationToken))
            .ToArray();

        // Accumulated directory entries keyed by directory path (forward-slash, no trailing slash)
        // Value: list of FileTreeEntry for that directory (files + resolved child dirs)
        var dirEntries = new Dictionary<string, List<FileTreeEntry>>(StringComparer.Ordinal);

        try
        {
            // Stream through sorted manifest entries
            await foreach (var manifestEntry in ReadManifestAsync(sortedManifestPath, cancellationToken))
            {
                var filePath = manifestEntry.Path;  // e.g., "photos/2024/june/a.jpg"
                var dirPath  = GetDirectoryPath(filePath);  // e.g., "photos/2024/june"
                var name     = Path.GetFileName(filePath);

                if (!dirEntries.TryGetValue(dirPath, out var list))
                    dirEntries[dirPath] = list = new List<FileTreeEntry>();

                list.Add(new FileEntry
                {
                    Name        = name,
                    ContentHash = manifestEntry.ContentHash,
                    Created  = manifestEntry.Created,
                    Modified = manifestEntry.Modified
                });
            }

            if (dirEntries.Count == 0)
            {
                pendingUploads.Writer.TryComplete();
                await Task.WhenAll(uploadTasks);
                return null;
            }

            // Ensure all intermediate directories exist in dirEntries
            // (directories that only contain subdirectories, not files, would otherwise be missing)
            var allDirs = new HashSet<string>(dirEntries.Keys, StringComparer.Ordinal);
            foreach (var dirPath in dirEntries.Keys.ToList())
            {
                var parent = GetDirectoryPath(dirPath);
                while (parent != string.Empty && !allDirs.Contains(parent))
                {
                    allDirs.Add(parent);
                    dirEntries[parent] = new List<FileTreeEntry>();
                    parent = GetDirectoryPath(parent);
                }
            }

            if (!dirEntries.ContainsKey(string.Empty))
                dirEntries[string.Empty] = new List<FileTreeEntry>();

            // Process directories bottom-up: deepest paths first (more slashes → deeper)
            var sortedDirs = dirEntries.Keys
                .OrderByDescending(d => d.Count(c => c == '/'))
                .ThenByDescending(d => d, StringComparer.Ordinal)
                .ToList();

            FileTreeHash? rootHash = null;

            foreach (var dirPath in sortedDirs)
            {
                var entries = dirEntries[dirPath];

                var tree = new FileTreeBlob { Entries = entries };
                var hash = FileTreeBlobSerializer.ComputeHash(tree, _encryption);

                // Dedup: upload only if not already cached/known remotely
                await QueueUploadAsync(hash, tree, pendingUploads.Writer, cancellationToken);

                var parentPath = GetDirectoryPath(dirPath);
                if (parentPath != dirPath && dirEntries.TryGetValue(parentPath, out var parentEntries))
                {
                    parentEntries.Add(new DirectoryEntry
                    {
                        Name         = GetLastSegment(dirPath) + "/",
                        FileTreeHash = hash
                    });
                }

                // If this directory has no parent (empty string = root), it is the root
                if (dirPath == string.Empty)
                    rootHash = hash;
            }

            if (rootHash is null)
                throw new InvalidOperationException("Filetree synchronization did not produce a root hash.");

            pendingUploads.Writer.TryComplete();
            await Task.WhenAll(uploadTasks);
            return rootHash;
        }
        catch (Exception ex)
        {
            pendingUploads.Writer.TryComplete(ex);
            await Task.WhenAll(uploadTasks);
            throw;
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
     /// Uploads a tree blob via <see cref="FileTreeService"/> if not already present remotely.
     /// </summary>
    private async Task QueueUploadAsync(
        FileTreeHash      treeHash,
        FileTreeBlob      tree,
        ChannelWriter<(FileTreeHash Hash, FileTreeBlob Tree)> uploadWriter,
        CancellationToken cancellationToken)
    {
        if (_fileTreeService.ExistsInRemote(treeHash)) return;
        await uploadWriter.WriteAsync((treeHash, tree), cancellationToken);
    }

    // ── Manifest streaming ────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ManifestEntry> ReadManifestAsync(
        string manifestPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(manifestPath, System.Text.Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return ManifestEntry.Parse(line);
        }
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the directory portion of a forward-slash path.
    /// e.g., "photos/2024/june/a.jpg" → "photos/2024/june"
    /// e.g., "a.jpg" → "" (root)
    /// </summary>
    private static string GetDirectoryPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? string.Empty : path[..idx];
    }

    /// <summary>
    /// Returns the last path segment (directory name without parent).
    /// e.g., "photos/2024/june" → "june"
    /// </summary>
    private static string GetLastSegment(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }
}
