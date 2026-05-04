using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Shared.LocalFile;

// ── Task 7.1: FilePair model ───────────────────────────────────────────────────

/// <summary>
/// Represents a local file (binary and/or pointer) to be processed by the archive pipeline.
///
/// Each <see cref="FilePair"/> has exactly one of:
/// - Both binary and pointer: normal archived file with up-to-date pointer
/// - Binary only: not yet archived (needs upload and pointer creation)
/// - Pointer only (thin archive): binary was removed, pointer has the content hash
///
/// Paths are always forward-slash-normalized and relative to the archive root.
/// </summary>
public sealed record FilePair
{
    /// <summary>
    /// Forward-slash-normalized path relative to the archive root (no leading slash).
    /// e.g. <c>photos/2024/june/a.jpg</c>
    /// </summary>
    public required RelativePath RelativePath { get; init; }

    /// <summary><c>true</c> if the binary file is present on disk.</summary>
    public required bool BinaryExists { get; init; }

    /// <summary><c>true</c> if a <c>.pointer.arius</c> file is present alongside the binary.</summary>
    public required bool PointerExists { get; init; }

    /// <summary>
    /// The hash stored in the pointer file, if the pointer exists and contains a valid hex hash.
    /// <c>null</c> when no pointer or when pointer content is invalid.
    /// </summary>
    public          ContentHash? PointerHash { get; init; }

    /// <summary>File size in bytes of the binary. <c>null</c> for pointer-only entries.</summary>
    public          long?   FileSize { get; init; }

    /// <summary>Creation timestamp of the binary (UTC). <c>null</c> for pointer-only entries.</summary>
    public          DateTimeOffset? Created  { get; init; }

    /// <summary>Last-modified timestamp of the binary (UTC). <c>null</c> for pointer-only entries.</summary>
    public          DateTimeOffset? Modified { get; init; }
}

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
public sealed class LocalFileEnumerator
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
    public IEnumerable<FilePair> Enumerate(LocalRootPath rootDirectory)
    {
        foreach (var (file, filePath) in EnumerateFilesDepthFirst(RelativePath.Root.RootedAt(rootDirectory)))
        {
            var relativePath = filePath.RelativePath;
            var relativeName = relativePath.ToString();

            if (relativeName.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Pointer file: skip if binary exists (already emitted with binary's pair)
                var binaryFileRelativeName  = relativeName[..^PointerSuffix.Length]; // infer the BinaryFile relative name from the pointer file name
                var binaryFileRelativePath = RelativePath.Parse(binaryFileRelativeName);
                var binaryFileFullPath = binaryFileRelativePath.RootedAt(rootDirectory).FullPath;

                if (File.Exists(binaryFileFullPath))
                    continue; // binary was/will be emitted as part of the binary's FilePair

                // Pointer-only (thin archive)
                var pointerHash = ReadPointerHash(file.FullName, relativeName);
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
                var pointerRel  = relativeName + PointerSuffix;
                var pointerPath = RelativePath.Parse(pointerRel).RootedAt(rootDirectory).FullPath;
                var hasPointer  = File.Exists(pointerPath);
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
    private ContentHash? ReadPointerHash(string fullPath, string relPath)
    {
        try
        {
            var content = File.ReadAllText(fullPath).Trim();
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
