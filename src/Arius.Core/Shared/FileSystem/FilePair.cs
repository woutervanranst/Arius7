using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents the archive-time view of one repository path, combining the binary file and its optional pointer file.
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
