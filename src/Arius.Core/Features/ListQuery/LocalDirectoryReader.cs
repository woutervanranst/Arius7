using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Reads one local directory into a <see cref="LocalDirectoryListing"/>: enumerates immediate
/// children (sorted, case-insensitive) and pairs each pointer sidecar with its binary. IO errors
/// degrade to an empty/partial listing with a warning instead of failing the walk.
/// </summary>
internal static class LocalDirectoryReader
{
    public static LocalDirectoryListing Read(RelativeFileSystem fileSystem, RelativePath directory, ILogger logger, FileExclusionFilter? filter = null)
    {
        // Apply the same exclusion policy as the archive enumerator to the local half only, so config-
        // excluded entries (@eaDir, thumbs.db, system/hidden) don't surface in `ls --local` as spurious
        // local-only rows. The remote/snapshot half is never filtered, so an older snapshot that still
        // contains such entries keeps listing them as repository rows.
        filter ??= FileExclusionFilter.None;

        var subdirectories = new HashSet<PathSegment>();
        try
        {
            foreach (var subdirectory in fileSystem.EnumerateDirectories(directory))
            {
                var name = subdirectory.Name;
                if (filter.ShouldExcludeDirectory(name, default))
                    continue;
                if (filter.RequiresAttributes && filter.ShouldExcludeDirectory(name, SafeGetAttributes(fileSystem, subdirectory)))
                    continue;
                subdirectories.Add(name);
            }
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
                .Where(file => !IsExcludedFile(fileSystem, file, filter))
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
                try
                {
                    var (created, modified) = fileSystem.GetTimestamps(path);
                    return (fileSystem.GetFileSize(path), created, modified);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not stat file: {Path}", path);
                    return (0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
                }
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
        var files           = new Dictionary<PathSegment, LocalFile>();
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

    /// <summary>
    /// Whether the enumerated path is excluded by policy. Keyed on the file's <i>logical</i> name: for a
    /// pointer sidecar (<c>thumbs.db.pointer.arius</c>) that is the binary it stands in for
    /// (<c>thumbs.db</c>), mirroring <c>LocalFileEnumerator</c> so an excluded file can't slip back in
    /// through a leftover pointer. The cheap name check runs first; attributes are only read when an
    /// attribute rule is active.
    /// </summary>
    private static bool IsExcludedFile(RelativeFileSystem fileSystem, RelativePath path, FileExclusionFilter filter)
    {
        var logicalName = path.IsPointerPath() ? path.ToBinaryPath().Name : path.Name;
        if (filter.ShouldExcludeFile(logicalName, default))
            return true;
        return filter.RequiresAttributes && filter.ShouldExcludeFile(logicalName, SafeGetAttributes(fileSystem, path));
    }

    /// <summary>
    /// Reads an entry's attributes for an attribute-based exclusion check, treating an unreadable entry
    /// as not-excluded. Must be per-entry safe: a throw here would otherwise be caught by the enumeration
    /// try/catch and drop the whole directory listing to empty.
    /// </summary>
    private static FileAttributes SafeGetAttributes(RelativeFileSystem fileSystem, RelativePath path)
    {
        try
        {
            return fileSystem.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}