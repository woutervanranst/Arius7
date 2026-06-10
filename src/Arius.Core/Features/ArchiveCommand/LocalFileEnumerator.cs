using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ArchiveCommand;

// ── Task 7.2, 7.3, 7.4, 7.5: File enumeration service ────────────────────────

/// <summary>
/// Enumerates local repository files and assembles <see cref="FilePair"/> values.
/// It exists to translate raw filesystem contents into Arius's archive-time local-file domain model,
/// with responsibility for recognizing pointer files, pairing them with binaries, and tolerating unreadable
/// or invalid pointer content without leaking host-path details into the rest of the core.
/// </summary>
internal sealed class LocalFileEnumerator
{
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
    /// When a binary file is encountered, its pointer counterpart is resolved from the
    /// rooted relative filesystem enumeration; the pair is yielded immediately.
    /// When a pointer file is encountered, if its binary exists it is skipped (already
    /// emitted as part of the binary's pair); otherwise it is yielded as pointer-only.
    /// No dictionaries or state-tracking collections are used.
    /// </summary>
    public IEnumerable<FilePair> Enumerate(LocalDirectory rootDirectory)
    {
        var fileSystem = new RelativeFileSystem(rootDirectory);

        foreach (var file in fileSystem.EnumerateFiles())
        {
            var relativePath = file.Path;

            if (!fileSystem.IsValidSymlink(relativePath))
            {
                _logger?.LogWarning("Skipping broken symlink: {RelPath}", relativePath);
                continue;
            }

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
                    RelativePath = binaryPath,
                    Binary       = null,
                    Pointer = new PointerFile
                    {
                        Path       = relativePath,
                        Hash       = pointerHash
                    }
                };
            }
            else
            {
                // Binary file: check for pointer via the rooted relative filesystem enumeration.
                var          pointerPath = relativePath.ToPointerPath();
                var          hasPointer  = fileSystem.FileExists(pointerPath);
                ContentHash? pointerHash = null;

                if (hasPointer)
                    pointerHash = ReadPointerHash(fileSystem, pointerPath);

                yield return new FilePair
                {
                    RelativePath = relativePath,
                    Binary = new BinaryFile
                    {
                        Path = relativePath,
                    },
                    Pointer = hasPointer
                        ? new PointerFile
                        {
                            Path       = pointerPath,
                            Hash       = pointerHash
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
                _logger?.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", path);
                return null;
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read pointer file: {RelPath}", path);
            return null;
        }
    }
}
