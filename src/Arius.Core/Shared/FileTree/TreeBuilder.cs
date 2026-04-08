using Arius.Core.Shared.Encryption;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Builds Merkle tree blobs bottom-up from a sorted manifest file.
///
/// Algorithm (tasks 5.6, 5.7, 5.8, 5.9):
/// 1. Stream sorted entries. Group by immediate parent directory.
/// 2. For each directory (deepest-first, bottom-up):
///    a. Collect file entries + already-computed child directory entries.
///    b. Build <see cref="TreeBlob"/>.
///    c. Compute tree hash.
///    d. Check via <see cref="TreeCacheService.ExistsInRemote"/> — if present, skip upload (dedup).
///    e. Upload new blob via <see cref="TreeCacheService.WriteAsync"/>; disk cache written automatically.
///    f. Cascade the directory hash up to the parent directory.
/// 3. Return root tree hash.
///
/// Empty directories are skipped (task 5.8): if no files reach a directory
/// (directly or recursively), that directory generates no tree blob.
/// </summary>
public sealed class TreeBuilder
{
    private readonly IEncryptionService  _encryption;
    private readonly TreeCacheService    _treeCache;

    /// <summary>
    /// Builds trees using shared services supplied by the caller/DI container.
    /// </summary>
    public TreeBuilder(
        IEncryptionService encryption,
        TreeCacheService   treeCache)
    {
        _encryption = encryption;
        _treeCache  = treeCache;
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full Merkle tree from a sorted manifest file and returns the root tree hash.
    /// Returns <c>null</c> if the manifest is empty (nothing to archive).
    /// </summary>
    public async Task<string?> BuildAsync(
        string            sortedManifestPath,
        CancellationToken cancellationToken = default)
    {
        // Ensure cache is validated before calling ExistsInRemote (idempotent — no-op if already validated).
        await _treeCache.ValidateAsync(cancellationToken);

        // Accumulated directory entries keyed by directory path (forward-slash, no trailing slash)
        // Value: list of TreeEntry for that directory (files + resolved child dirs)
        var dirEntries = new Dictionary<string, List<TreeEntry>>(StringComparer.Ordinal);

        // Stream through sorted manifest entries
        await foreach (var manifestEntry in ReadManifestAsync(sortedManifestPath, cancellationToken))
        {
            var filePath = manifestEntry.Path;  // e.g., "photos/2024/june/a.jpg"
            var dirPath  = GetDirectoryPath(filePath);  // e.g., "photos/2024/june"
            var name     = Path.GetFileName(filePath);

            if (!dirEntries.TryGetValue(dirPath, out var list))
                dirEntries[dirPath] = list = new List<TreeEntry>();

            list.Add(new TreeEntry
            {
                Name     = name,
                Type     = TreeEntryType.File,
                Hash     = manifestEntry.ContentHash,
                Created  = manifestEntry.Created,
                Modified = manifestEntry.Modified
            });
        }

        if (dirEntries.Count == 0) return null;

        // Ensure all intermediate directories exist in dirEntries
        // (directories that only contain subdirectories, not files, would otherwise be missing)
        var allDirs = new HashSet<string>(dirEntries.Keys, StringComparer.Ordinal);
        foreach (var dirPath in dirEntries.Keys.ToList())
        {
            var parent = GetDirectoryPath(dirPath);
            while (parent != string.Empty && !allDirs.Contains(parent))
            {
                allDirs.Add(parent);
                dirEntries[parent] = new List<TreeEntry>();
                parent = GetDirectoryPath(parent);
            }
        }

        // Process directories bottom-up: deepest paths first (more slashes → deeper)
        var sortedDirs = dirEntries.Keys
            .OrderByDescending(d => d.Count(c => c == '/'))
            .ThenByDescending(d => d, StringComparer.Ordinal)
            .ToList();

        // Map from directory path → its computed tree hash (for cascading to parents)
        var dirHashMap = new Dictionary<string, string>(StringComparer.Ordinal);

        string? rootHash = null;

        foreach (var dirPath in sortedDirs)
        {
            var entries = dirEntries[dirPath];

            // Inject already-computed child directory entries
            // (any immediate child directories of dirPath that have been processed)
            foreach (var (childDirPath, childHash) in dirHashMap)
            {
                var childParent = GetDirectoryPath(childDirPath);
                if (childParent == dirPath)
                {
                    var childName = GetLastSegment(childDirPath);
                    entries.Add(new TreeEntry
                    {
                        Name     = childName + "/",
                        Type     = TreeEntryType.Dir,
                        Hash     = childHash,
                        Created  = null,
                        Modified = null
                    });
                }
            }

            var tree = new TreeBlob { Entries = entries };
            var hash = TreeBlobSerializer.ComputeHash(tree, _encryption);

            // Dedup: upload only if not already cached/known remotely
            await EnsureUploadedAsync(hash, tree, cancellationToken);

            dirHashMap[dirPath] = hash;

            // If this directory has no parent (empty string = root), it is the root
            if (dirPath == string.Empty || !dirPath.Contains('/'))
            {
                // Check if there's a parent for this dir
                var parent = GetDirectoryPath(dirPath);
                if (parent == dirPath) // root
                    rootHash = hash;
                // else: will be cascaded to parent when parent is processed
            }
        }

        // The root is the directory with the shallowest path
        // Find the directory closest to root (fewest slashes)
        if (rootHash is null)
        {
            var rootDir = dirHashMap.Keys
                .OrderBy(d => d.Count(c => c == '/'))
                .ThenBy(d => d, StringComparer.Ordinal)
                .First();

            // Build the root tree — all immediate children of "" (root)
            // If all files are in subdirectories, we need to build root blob too
            rootHash = await BuildRootBlobAsync(dirHashMap, dirEntries, cancellationToken);
        }

        return rootHash;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<string> BuildRootBlobAsync(
        Dictionary<string, string>       dirHashMap,
        Dictionary<string, List<TreeEntry>> dirEntries,
        CancellationToken                cancellationToken)
    {
        // Find top-level entries (directories/files whose parent is "")
        var rootEntryList = new List<TreeEntry>();

        // Top-level files (path has no slash)
        if (dirEntries.TryGetValue(string.Empty, out var rootFiles))
            rootEntryList.AddRange(rootFiles);

        // Top-level directories (single-segment paths that are in dirHashMap)
        foreach (var (dirPath, hash) in dirHashMap)
        {
            if (dirPath == string.Empty) continue;
            var parentPath = GetDirectoryPath(dirPath);
            if (parentPath == string.Empty)
            {
                var dirName = GetLastSegment(dirPath);
                rootEntryList.Add(new TreeEntry
                {
                    Name     = dirName + "/",
                    Type     = TreeEntryType.Dir,
                    Hash     = hash,
                    Created  = null,
                    Modified = null
                });
            }
        }

        var rootTree = new TreeBlob { Entries = rootEntryList };
        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, _encryption);
        await EnsureUploadedAsync(rootHash, rootTree, cancellationToken);
        return rootHash;
    }

    /// <summary>
    /// Uploads a tree blob via <see cref="TreeCacheService"/> if not already present remotely.
    /// </summary>
    private async Task EnsureUploadedAsync(
        string            treeHash,
        TreeBlob          tree,
        CancellationToken cancellationToken)
    {
        if (_treeCache.ExistsInRemote(treeHash)) return;
        await _treeCache.WriteAsync(treeHash, tree, cancellationToken);
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
