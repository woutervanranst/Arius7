using Microsoft.Extensions.Logging;

namespace Arius.Core.LocalFile;

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
    public required string RelativePath { get; init; }

    /// <summary><c>true</c> if the binary file is present on disk.</summary>
    public required bool BinaryExists { get; init; }

    /// <summary><c>true</c> if a <c>.pointer.arius</c> file is present alongside the binary.</summary>
    public required bool PointerExists { get; init; }

    /// <summary>
    /// The hash stored in the pointer file, if the pointer exists and contains a valid hex hash.
    /// <c>null</c> when no pointer or when pointer content is invalid.
    /// </summary>
    public          string? PointerHash { get; init; }

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
    /// Enumerates all <see cref="FilePair"/> objects under <paramref name="rootDirectory"/>.
    /// Depth-first: directories are processed before their siblings.
    /// </summary>
    public IEnumerable<FilePair> Enumerate(string rootDirectory)
    {
        // Collect all files with a depth-first walk
        var allFiles = EnumerateFilesDepthFirst(rootDirectory);

        // Separate binaries and pointers
        var binaries = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var pointers = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            var rel = NormalizePath(Path.GetRelativePath(rootDirectory, file.FullName));
            if (rel.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
                pointers[rel] = file;
            else
                binaries[rel] = file;
        }

        // Task 7.4: Assemble FilePairs
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Binary files (with or without pointer)
        foreach (var (relPath, binaryInfo) in binaries)
        {
            var pointerRel = relPath + PointerSuffix;
            var hasPointer = pointers.TryGetValue(pointerRel, out var pointerInfo);
            string? pointerHash = null;

            if (hasPointer)
                pointerHash = ReadPointerHash(pointerInfo!.FullName, pointerRel);

            emitted.Add(pointerRel); // Mark as paired

            yield return new FilePair
            {
                RelativePath  = relPath,
                BinaryExists  = true,
                PointerExists = hasPointer,
                PointerHash   = pointerHash,
                FileSize      = binaryInfo.Length,
                Created       = new DateTimeOffset(binaryInfo.CreationTimeUtc,  TimeSpan.Zero),
                Modified      = new DateTimeOffset(binaryInfo.LastWriteTimeUtc, TimeSpan.Zero)
            };
        }

        // Orphan pointer files (pointer-only / thin archive)
        foreach (var (pointerRel, pointerInfo) in pointers)
        {
            if (emitted.Contains(pointerRel)) continue; // already paired

            var pointerHash = ReadPointerHash(pointerInfo.FullName, pointerRel);

            yield return new FilePair
            {
                RelativePath  = pointerRel, // pointer path itself
                BinaryExists  = false,
                PointerExists = true,
                PointerHash   = pointerHash,
                FileSize      = null,
                Created       = null,
                Modified      = null
            };
        }
    }

    // ── Task 7.3: Pointer detection ───────────────────────────────────────────

    /// <summary>Reads and validates the hash from a pointer file. Returns <c>null</c> on invalid content.</summary>
    private string? ReadPointerHash(string fullPath, string relPath)
    {
        try
        {
            var content = File.ReadAllText(fullPath).Trim();
            if (!IsValidHex(content))
            {
                _logger?.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", relPath);
                return null;
            }
            return content;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read pointer file: {RelPath}", relPath);
            return null;
        }
    }

    private static bool IsValidHex(string s) =>
        s.Length > 0 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

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
