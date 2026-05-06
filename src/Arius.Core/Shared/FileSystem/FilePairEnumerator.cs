using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Enumerates local files and assembles <see cref="FilePair"/> objects.
///
/// Rules:
/// - Files ending in <c>.pointer.arius</c> are always treated as pointer files.
/// - All other files are treated as binaries.
/// - Depth-first enumeration (for directory affinity with the tar builder).
/// - Inaccessible files are skipped with a warning logged.
/// - Pointer file content must be a valid hex string; invalid content is warned and ignored.
/// - Paths are normalized to forward slashes (task 7.5).
/// </summary>
public sealed class FilePairEnumerator
{

    private readonly ILogger<FilePairEnumerator>? _logger;

    public FilePairEnumerator(ILogger<FilePairEnumerator>? logger = null)
    {
        _logger = logger;
    }

    // ── Task 7.2: Depth-first enumeration ────────────────────────────────────

    /// <summary>
    /// Enumerates all <see cref="FilePair"/> objects under <paramref name="rootDirectory"/>
    /// using a single-pass depth-first walk.
    ///
    /// When a binary file is encountered, <see cref="File.Exists"/> is used to check for
    /// its pointer counterpart; the pair is yielded immediately without buffering.
    /// When a pointer file is encountered, if its binary exists it is skipped (already
    /// emitted as part of the binary's pair); otherwise it is yielded as pointer-only.
    /// No dictionaries or state-tracking collections are used.
    /// </summary>
    public IEnumerable<FilePair> Enumerate(LocalRootPath rootDirectory)
    {
        foreach (var (file, filePath) in EnumerateFilesDepthFirst(RelativePath.Root.RootedAt(rootDirectory)))
        {
            var relativePath = filePath.RelativePath;
            if (relativePath.IsPointerFilePath())
            {
                // Pointer file: skip if binary exists (already emitted with binary's pair)
                var binaryFileRelativePath = relativePath.ToBinaryFilePath();
                var binaryFilePath = binaryFileRelativePath.RootedAt(rootDirectory);

                if (binaryFilePath.ExistsFile)
                    continue; // binary was/will be emitted as part of the binary's FilePair

                // Pointer-only (thin archive)
                var pointerHash = ReadPointerHash(filePath, relativePath);
                yield return new FilePair
                {
                    RelativePath  = binaryFileRelativePath,
                    BinaryExists  = false,
                    PointerExists = true,
                    PointerHash   = pointerHash,
                    FileSize      = null,
                    Created       = null,
                    Modified      = null
                };
            }
            else
            {
                // Binary file: check for pointer via File.Exists
                var pointerRel  = relativePath.ToPointerFilePath();
                var pointerPath = pointerRel.RootedAt(rootDirectory);
                var hasPointer  = pointerPath.ExistsFile;
                ContentHash? pointerHash = null;

                if (hasPointer)
                    pointerHash = ReadPointerHash(pointerPath, pointerRel);

                yield return new FilePair
                {
                    RelativePath  = relativePath,
                    BinaryExists  = true,
                    PointerExists = hasPointer,
                    PointerHash   = pointerHash,
                    FileSize      = file.Length,
                    Created       = new DateTimeOffset(file.CreationTimeUtc,  TimeSpan.Zero),
                    Modified      = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
                };
            }
        }
    }

    // ── Task 7.3: Pointer detection ───────────────────────────────────────────

    /// <summary>Reads and validates the hash from a pointer file. Returns <c>null</c> on invalid content.</summary>
    private ContentHash? ReadPointerHash(RootedPath fullPath, RelativePath relPath)
    {
        try
        {
            var content = fullPath.ReadAllText().Trim();
            if (!ContentHash.TryParse(content, out var hash))
            {
                _logger?.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", relPath);
                return null;
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read pointer file: {RelPath}", relPath);
            return null;
        }
    }

    // ── Task 7.5: Path normalization ──────────────────────────────────────────

    /// <summary>Normalizes a path to forward slashes, no leading slash.</summary>
    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    // ── Depth-first file walk ─────────────────────────────────────────────────

    private IEnumerable<(FileInfo File, RootedPath Path)> EnumerateFilesDepthFirst(RootedPath directory)
    {
        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(directory.FullPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not access directory: {Directory}", directory.FullPath);
            yield break;
        }

        // Files in this directory first
        IEnumerable<FileInfo> files;
        try
        {
            files = dirInfo.EnumerateFiles();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not enumerate files in: {Directory}", directory.FullPath);
            files = [];
        }

        foreach (var file in files)
        {
            var filePath = (directory.RelativePath / PathSegment.Parse(file.Name)).RootedAt(directory.Root);
            yield return (file, filePath);
        }

        // Then recurse into subdirectories (depth-first)
        IEnumerable<DirectoryInfo> subdirs;
        try
        {
            subdirs = dirInfo.EnumerateDirectories();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", directory.FullPath);
            subdirs = [];
        }

        foreach (var sub in subdirs)
        {
            var subdirectory = (directory.RelativePath / PathSegment.Parse(sub.Name)).RootedAt(directory.Root);
            foreach (var file in EnumerateFilesDepthFirst(subdirectory))
                yield return file;
        }
    }
}
