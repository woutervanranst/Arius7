using Arius.Core.Shared.FileTree;

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