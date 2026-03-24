using Arius.Core.Encryption;
using Arius.Core.Storage;

namespace Arius.Core.FileTree;

/// <summary>
/// One entry written to the manifest temp file during the archive pipeline.
/// Format (tab-separated, one line each): <c>path\thash\tcreated\tmodified\n</c>
/// <para>
/// <c>path</c> is always forward-slash-normalized and relative to the archive root.
/// <c>hash</c> is the content-hash (hex).
/// Timestamps are ISO-8601 round-trip ("O"), UTC.
/// </para>
/// </summary>
public sealed record ManifestEntry(
    string         Path,
    string         ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified)
{
    private const char Sep = '\t';

    /// <summary>Serializes to a manifest line (no trailing newline).</summary>
    public string Serialize() =>
        $"{Path}{Sep}{ContentHash}{Sep}{Created:O}{Sep}{Modified:O}";

    /// <summary>Parses a manifest line. Throws on invalid input.</summary>
    public static ManifestEntry Parse(string line)
    {
        var parts = line.Split(Sep);
        if (parts.Length != 4)
            throw new FormatException($"Invalid manifest line (expected 4 tab-separated fields): '{line}'");

        return new ManifestEntry(
            Path        : parts[0],
            ContentHash : parts[1],
            Created     : DateTimeOffset.Parse(parts[2]),
            Modified    : DateTimeOffset.Parse(parts[3]));
    }
}

/// <summary>
/// Writes completed-file entries to an unsorted temp file during the archive pipeline.
/// Call <see cref="FlushAsync"/> / Dispose when done.
/// Thread-safe for concurrent writers.
/// </summary>
public sealed class ManifestWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string TempFilePath { get; }

    public ManifestWriter(string tempFilePath)
    {
        TempFilePath = tempFilePath;
        _writer      = new StreamWriter(
            new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true),
            System.Text.Encoding.UTF8,
            leaveOpen: false);
    }

    /// <summary>Appends a manifest entry. Thread-safe.</summary>
    public async Task AppendAsync(ManifestEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(entry.Serialize().AsMemory(), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        _writer.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// External sort of the manifest file: reads all entries, sorts by path, rewrites.
/// At 500M × ~80 bytes this is ~40 GB; a chunked merge sort would be needed at that
/// scale. For the initial implementation we use an in-process LINQ sort on the file
/// (sufficient for development/testing; the hook is isolated so it can be swapped
/// for a true external sort later).
/// </summary>
public static class ManifestSorter
{
    /// <summary>
    /// Sorts <paramref name="manifestPath"/> in place by path (ordinal ascending).
    /// Returns the same path for convenience.
    /// </summary>
    public static async Task<string> SortAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        // Read all lines
        var lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken);

        // Parse → sort → serialize
        var sorted = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ManifestEntry.Parse)
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        // Rewrite in place
        await using var writer = new StreamWriter(
            new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true));

        foreach (var entry in sorted)
            await writer.WriteLineAsync(entry.Serialize().AsMemory(), cancellationToken);

        return manifestPath;
    }
}

/// <summary>
/// Builds Merkle tree blobs bottom-up from a sorted manifest file.
///
/// Algorithm (tasks 5.6, 5.7, 5.8, 5.9):
/// 1. Stream sorted entries. Group by immediate parent directory.
/// 2. For each directory (deepest-first, bottom-up):
///    a. Collect file entries + already-computed child directory entries.
///    b. Build <see cref="TreeBlob"/>.
///    c. Compute tree hash.
///    d. Check local disk cache — if hit, skip upload (dedup).
///    e. Check remote <c>filetrees/&lt;hash&gt;</c> — if exists, save to disk cache, skip upload.
///    f. Upload new blob to <c>filetrees/&lt;hash&gt;</c>; save bytes to disk cache.
///    g. Cascade the directory hash up to the parent directory.
/// 3. Return root tree hash.
///
/// Empty directories are skipped (task 5.8): if no files reach a directory
/// (directly or recursively), that directory generates no tree blob.
/// </summary>
public sealed class TreeBuilder
{
    private readonly IBlobStorageService _blobs;
    private readonly IEncryptionService  _encryption;
    private readonly string              _diskCacheDir;

    public TreeBuilder(
        IBlobStorageService blobs,
        IEncryptionService  encryption,
        string              accountName,
        string              containerName)
    {
        _blobs        = blobs;
        _encryption   = encryption;
        _diskCacheDir = GetDiskCacheDirectory(accountName, containerName);
        Directory.CreateDirectory(_diskCacheDir);
    }

    // ── Cache directory ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the local disk cache directory for tree blobs:
    /// <c>~/.arius/{accountName}-{containerName}/filetrees/</c>
    /// </summary>
    public static string GetDiskCacheDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".arius", ChunkIndex.ChunkIndexService.GetRepoDirectoryName(accountName, containerName), "filetrees");
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
        // Accumulated directory entries keyed by directory path (forward-slash, no trailing slash)
        // Value: list of TreeEntry for that directory (files + resolved child dirs)
        var dirEntries = new Dictionary<string, List<TreeEntry>>(StringComparer.Ordinal);

        // Stream through sorted manifest entries
        await foreach (var manifestEntry in ReadManifestAsync(sortedManifestPath, cancellationToken))
        {
            var filePath = manifestEntry.Path;  // e.g., "photos/2024/june/a.jpg"
            var dirPath  = GetDirectoryPath(filePath);  // e.g., "photos/2024/june"
            var name     = System.IO.Path.GetFileName(filePath);

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

            // Dedup: upload only if not already on disk cache or in blob storage
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
    /// Uploads a tree blob if not already present on disk cache or in blob storage.
    /// Saves to disk cache if uploaded or found in blob storage.
    /// </summary>
    private async Task EnsureUploadedAsync(
        string            treeHash,
        TreeBlob          tree,
        CancellationToken cancellationToken)
    {
        var diskPath = Path.Combine(_diskCacheDir, treeHash);

        // Disk cache hit → no upload needed (blob was already uploaded in a prior run)
        if (File.Exists(diskPath)) return;

        var blobName = BlobPaths.FileTree(treeHash);
        var json     = TreeBlobSerializer.Serialize(tree);

        // Remote blob exists? Save to disk cache, skip upload.
        var meta = await _blobs.GetMetadataAsync(blobName, cancellationToken);
        if (meta.Exists)
        {
            File.WriteAllBytes(diskPath, json);
            return;
        }

        // Upload new blob
        await _blobs.UploadAsync(
            blobName,
            new MemoryStream(json),
            new Dictionary<string, string>(),
            BlobTier.Cool,
            ContentTypes.FileTree,
            overwrite: false,
            cancellationToken: cancellationToken);

        // Save to disk cache
        File.WriteAllBytes(diskPath, json);
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
