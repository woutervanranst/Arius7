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
    /// using a single-pass depth-first walk that prunes excluded directory subtrees.
    ///
    /// Directories whose name (or, when configured, <see cref="FileAttributes.System"/>/
    /// <see cref="FileAttributes.Hidden"/>) matches <paramref name="filter"/> are skipped entirely —
    /// their contents are never enumerated. Files matching the filter are skipped individually.
    ///
    /// When a binary file is encountered, its pointer counterpart is resolved from the
    /// rooted relative filesystem enumeration; the pair is yielded immediately.
    /// When a pointer file is encountered, if its binary exists it is skipped (already
    /// emitted as part of the binary's pair); otherwise it is yielded as pointer-only.
    /// No dictionaries or state-tracking collections are used.
    /// </summary>
    /// <param name="rootDirectory">The repository root to enumerate.</param>
    /// <param name="filter">
    /// Exclusion policy. When <c>null</c>, nothing is excluded (preserves the unfiltered walk).
    /// </param>
    public IEnumerable<FilePair> Enumerate(LocalDirectory rootDirectory, FileExclusionFilter? filter = null)
    {
        var fileSystem = new RelativeFileSystem(rootDirectory);
        return EnumerateDirectory(fileSystem, RelativePath.Root, filter ?? FileExclusionFilter.None);
    }

    private IEnumerable<FilePair> EnumerateDirectory(RelativeFileSystem fileSystem, RelativePath directory, FileExclusionFilter filter)
    {
        // ── Files in this directory ───────────────────────────────────────────
        foreach (var relativePath in SafeEnumerate(fileSystem.EnumerateFiles(directory, SearchOption.TopDirectoryOnly), directory))
        {
            // Exclusion is keyed on the file's *logical* name: for a pointer-only file (thin archive)
            // the logical name is the binary it stands in for (thumbs.db, not thumbs.db.pointer.arius),
            // so an excluded file can't slip back into the snapshot through its leftover pointer.
            var logicalName = relativePath.IsPointerPath() ? relativePath.ToBinaryPath().Name : relativePath.Name;

            // Name-based exclusion is cheapest and needs no stat.
            if (filter.ShouldExcludeFile(logicalName, default))
            {
                _logger?.LogWarning("Skipping excluded file: {RelPath}", relativePath);
                continue;
            }

            if (!fileSystem.IsValidSymlink(relativePath))
            {
                _logger?.LogWarning("Skipping broken symlink: {RelPath}", relativePath);
                continue;
            }

            // Attribute-based exclusion only stats the entry when an attribute rule is active.
            if (filter.RequiresAttributes && filter.ShouldExcludeFile(logicalName, SafeGetAttributes(fileSystem, relativePath)))
            {
                _logger?.LogWarning("Skipping excluded file (System/Hidden attribute): {RelPath}", relativePath);
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

        // ── Subdirectories (pruned) ───────────────────────────────────────────
        foreach (var subDirectory in SafeEnumerate(fileSystem.EnumerateDirectories(directory), directory))
        {
            // A dangling directory symlink can't be descended into — recursing would fault the whole
            // scan. Skip it (mirrors the per-file symlink check above; the old flat AllDirectories walk
            // skipped inaccessible entries by default).
            if (!fileSystem.IsValidSymlink(subDirectory))
            {
                _logger?.LogWarning("Skipping broken directory symlink: {RelPath}", subDirectory);
                continue;
            }

            var attributes = filter.RequiresAttributes ? SafeGetAttributes(fileSystem, subDirectory) : default;
            if (filter.ShouldExcludeDirectory(subDirectory.Name, attributes))
            {
                _logger?.LogWarning("Skipping excluded directory: {RelPath}", subDirectory);
                continue;
            }

            foreach (var pair in EnumerateDirectory(fileSystem, subDirectory, filter))
                yield return pair;
        }
    }

    /// <summary>
    /// Enumerates a directory listing, logging a warning and stopping if the directory cannot be read
    /// (e.g. permission denied, or a path that vanished mid-walk). Yields lazily so a huge directory is
    /// never materialized and the walk stays memory-bounded. This mirrors the old flat
    /// <c>EnumerateFiles(AllDirectories)</c> walk, which skipped inaccessible directories by default.
    /// </summary>
    internal IEnumerable<RelativePath> SafeEnumerate(IEnumerable<RelativePath> listing, RelativePath directory)
    {
        using var enumerator = listing.GetEnumerator();
        while (true)
        {
            RelativePath entry;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                entry = enumerator.Current;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger?.LogWarning(ex, "Skipping unreadable directory: {RelPath}", directory);
                yield break;
            }

            yield return entry;
        }
    }

    /// <summary>
    /// Reads an entry's attributes, returning <c>default</c> when the entry is unreadable
    /// (e.g. a broken symlink). Attribute-based exclusion then simply does not apply; broken
    /// symlinks are still handled by the symlink-validity check.
    /// </summary>
    private static FileAttributes SafeGetAttributes(RelativeFileSystem fileSystem, RelativePath path)
    {
        try
        {
            return fileSystem.GetAttributes(path);
        }
        catch
        {
            return default;
        }
    }

    // ── Task 7.3: Pointer detection ───────────────────────────────────────────

    /// <summary>Reads and validates the hash from a pointer file. Returns <c>null</c> on invalid content.</summary>
    private ContentHash? ReadPointerHash(RelativeFileSystem fileSystem, RelativePath path)
    {
        try
        {
            var content = fileSystem.ReadAllText(path);
            if (!PointerFileFormat.TryParseHash(content, out var hash))
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
