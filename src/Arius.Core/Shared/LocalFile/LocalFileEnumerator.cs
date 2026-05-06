using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Shared.LocalFile;

// ── Task 7.2, 7.3, 7.4, 7.5: File enumeration service ────────────────────────

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
internal sealed class LocalFileEnumerator
{
    private const string PointerSuffix = ".pointer.arius";

    private readonly ILogger<LocalFileEnumerator>? _logger;

    public LocalFileEnumerator(ILogger<LocalFileEnumerator>? logger = null)
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
    public IEnumerable<FilePair> Enumerate(string rootDirectory)
    {
        var fileSystem = new RelativeFileSystem(LocalDirectory.Parse(Path.GetFullPath(rootDirectory)));

        foreach (var file in fileSystem.EnumerateFiles())
        {
            var relativePath = file.Path;

            if (relativePath.IsPointerPath())
            {
                // Pointer file: skip if binary exists (already emitted with binary's pair)
                var binaryPath = relativePath.ToBinaryPath();

                if (fileSystem.FileExists(binaryPath))
                    continue; // binary was/will be emitted as part of the binary's FilePair

                // Pointer-only (thin archive)
                var pointerHash = ReadPointerHash(fileSystem, relativePath);
                yield return new FilePair
                {
                    Path = binaryPath,
                    Binary = null,
                    Pointer = new PointerFile
                    {
                        Path = relativePath,
                        BinaryPath = binaryPath,
                        Hash = pointerHash
                    }
                };
            }
            else
            {
                // Binary file: check for pointer via File.Exists
                var pointerPath = relativePath.ToPointerPath();
                var hasPointer  = fileSystem.FileExists(pointerPath);
                ContentHash? pointerHash = null;

                if (hasPointer)
                    pointerHash = ReadPointerHash(fileSystem, pointerPath);

                yield return new FilePair
                {
                    Path = relativePath,
                    Binary = new BinaryFile
                    {
                        Path = relativePath,
                        Size = file.Size,
                        Created = file.Created,
                        Modified = file.Modified
                    },
                    Pointer = hasPointer
                        ? new PointerFile
                        {
                            Path = pointerPath,
                            BinaryPath = relativePath,
                            Hash = pointerHash
                        }
                        : null
                };
            }
        }
    }

    // ── Task 7.3: Pointer detection ───────────────────────────────────────────

    /// <summary>Reads and validates the hash from a pointer file. Returns <c>null</c> on invalid content.</summary>
    private ContentHash? ReadPointerHash(RelativeFileSystem fileSystem, RelativePath path)
    {
        try
        {
            var content = fileSystem.ReadAllText(path).Trim();
            if (!ContentHash.TryParse(content, out var hash))
            {
                _logger?.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", path.ToString());
                return null;
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read pointer file: {RelPath}", path.ToString());
            return null;
        }
    }

    // ── Task 7.5: Path normalization ──────────────────────────────────────────

    /// <summary>Normalizes a path to forward slashes, no leading slash.</summary>
    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    // ── Depth-first file walk ─────────────────────────────────────────────────

    private IEnumerable<FileInfo> EnumerateFilesDepthFirst(string directory)
    {
        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(directory);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not access directory: {Directory}", directory);
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
            _logger?.LogWarning(ex, "Could not enumerate files in: {Directory}", directory);
            files = [];
        }

        foreach (var file in files)
            yield return file;

        // Then recurse into subdirectories (depth-first)
        IEnumerable<DirectoryInfo> subdirs;
        try
        {
            subdirs = dirInfo.EnumerateDirectories();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", directory);
            subdirs = [];
        }

        foreach (var sub in subdirs)
        {
            foreach (var file in EnumerateFilesDepthFirst(sub.FullName))
                yield return file;
        }
    }
}
