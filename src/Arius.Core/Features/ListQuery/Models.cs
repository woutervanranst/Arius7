using Arius.Core.Shared.FileTree;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// What the repository knows about one directory: the persisted filetree node, split into files
/// and subdirectories. The mirror of <see cref="LocalDirectoryListing"/>.
/// </summary>
internal sealed record RemoteDirectoryListing(
    IReadOnlyList<FileEntry>      Files,          // tree order — the listing's reference order
    IReadOnlyList<DirectoryEntry> Subdirectories)
{
    public static readonly RemoteDirectoryListing Empty = new([], []);

    public static RemoteDirectoryListing From(IReadOnlyList<FileTreeEntry> treeEntries) =>
        new(
            treeEntries.OfType<FileEntry>().ToList(),
            treeEntries.OfType<DirectoryEntry>().ToList());
}

/// <summary>
/// What the local filesystem knows about the same directory: immediate child files (pointer and
/// binary paired under the binary name) and subdirectory names.
/// The mirror of <see cref="RemoteDirectoryListing"/>.
/// </summary>
internal sealed record LocalDirectoryListing(
    Dictionary<PathSegment, LocalFile> Files,     // keyed by binary name; consumed during the merge
    IReadOnlySet<PathSegment>          Subdirectories)
{
    /// <summary>Fresh instance per call: the merge mutates <see cref="Files"/>.</summary>
    public static LocalDirectoryListing Empty => new([], new HashSet<PathSegment>());
}

/// <summary>
/// One logical file on disk: the binary and its <c>.pointer.arius</c> sidecar count as a single
/// file, keyed by the binary name. Size and timestamps are <c>null</c> when only the pointer exists.
/// </summary>
internal sealed record LocalFile(
    PathSegment     Name,
    bool            BinaryExists,
    bool            PointerExists,
    long?           Size,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);

/// <summary>
/// Reads one local directory into a <see cref="LocalDirectoryListing"/>: enumerates immediate
/// children (sorted, case-insensitive) and pairs each pointer sidecar with its binary. IO errors
/// degrade to an empty/partial listing with a warning instead of failing the walk.
/// </summary>
internal static class LocalDirectoryReader
{
    public static LocalDirectoryListing Read(RelativeFileSystem fileSystem, RelativePath directory, ILogger logger)
    {
        var subdirectories = new HashSet<PathSegment>();
        try
        {
            foreach (var subdirectory in fileSystem.EnumerateDirectories(directory))
                subdirectories.Add(subdirectory.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", directory);
        }

        IReadOnlyList<RelativePath> filePaths;
        try
        {
            // Immediate children only: nested files are handled when their own directory is walked,
            // which keeps memory bounded by directory width.
            filePaths = fileSystem.EnumerateFiles(directory, SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, PathSegmentOrdinalIgnoreCaseComparer.Instance)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate files in: {Directory}", directory);
            filePaths = [];
        }

        var files = PairFiles(
            filePaths,
            fileSystem.FileExists,
            path =>
            {
                var (created, modified) = fileSystem.GetTimestamps(path);
                return (fileSystem.GetFileSize(path), created, modified);
            });

        return new LocalDirectoryListing(files, subdirectories);
    }

    /// <summary>
    /// Pairs each <c>.pointer.arius</c> sidecar with its binary into one <see cref="LocalFile"/>
    /// per logical file. Counterparts already present in the enumerated set are never re-probed
    /// on disk; <paramref name="stat"/> is only consulted for existing binaries.
    /// </summary>
    internal static Dictionary<PathSegment, LocalFile> PairFiles(
        IEnumerable<RelativePath> filePaths,
        Func<RelativePath, bool> fileExists,
        Func<RelativePath, (long Size, DateTimeOffset Created, DateTimeOffset Modified)> stat)
    {
        var files = new Dictionary<PathSegment, LocalFile>();
        var enumeratedPaths = filePaths.ToHashSet();

        foreach (var path in filePaths)
        {
            if (path.IsPointerPath())
            {
                var binaryPath = path.ToBinaryPath();
                var binaryName = binaryPath.Name;

                // The binary was already recorded (possibly under a case-variant pointer name):
                // just mark the pointer on it.
                if (files.TryGetValue(binaryName, out var pairedBinary))
                {
                    files[binaryName] = pairedBinary with { PointerExists = true };
                    continue;
                }

                // The binary is still to come in this enumeration (or exists outside it):
                // the binary pass records the pair.
                if (enumeratedPaths.Contains(binaryPath) || fileExists(binaryPath))
                    continue;

                // Pointer without a binary: the logical file exists on disk only as a pointer.
                files[binaryName] = new LocalFile(binaryName, BinaryExists: false, PointerExists: true, Size: null, Created: null, Modified: null);
            }
            else
            {
                var (size, created, modified) = stat(path);
                files[path.Name] = new LocalFile(
                    path.Name,
                    BinaryExists: true,
                    PointerExists: enumeratedPaths.Contains(path.ToPointerPath()),
                    size, created, modified);
            }
        }

        return files;
    }
}

internal sealed class PathSegmentOrdinalIgnoreCaseComparer : IComparer<PathSegment>
{
    public static PathSegmentOrdinalIgnoreCaseComparer Instance { get; } = new();

    public int Compare(PathSegment x, PathSegment y) =>
        x.Compare(y, StringComparer.OrdinalIgnoreCase);
}
