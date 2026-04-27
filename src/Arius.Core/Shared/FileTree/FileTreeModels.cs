using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Type discriminator for entries in a tree blob.
/// </summary>
public enum FileTreeEntryType
{
    File,
    Dir
}

/// <summary>
/// One entry in a <see cref="FileTreeBlob"/>.
/// Files carry a content-hash; directories carry a tree-hash.
/// Timestamps are present for file entries (UTC, ISO-8601). Directory entries omit timestamps.
/// </summary>
public abstract record FileTreeEntry
{
    /// <summary>File or directory name (not a full path).</summary>
    public required string         Name     { get; init; }

    /// <summary>Entry type: <c>file</c> or <c>dir</c>.</summary>
    public abstract FileTreeEntryType Type { get; }
}

public sealed record FileEntry : FileTreeEntry
{
    public override FileTreeEntryType Type => FileTreeEntryType.File;

    /// <summary>Content-hash for file entries. Lowercase hex SHA-256 (64 chars).</summary>
    public required ContentHash ContentHash { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public required DateTimeOffset Modified { get; init; }
}

public sealed record DirectoryEntry : FileTreeEntry
{
    public override FileTreeEntryType Type => FileTreeEntryType.Dir;

    /// <summary>Tree-hash for directory entries. Lowercase hex SHA-256 (64 chars).</summary>
    public required FileTreeHash FileTreeHash { get; init; }
}

/// <summary>
/// A Merkle tree node representing a single directory.
/// Serialized as text (one entry per line) and uploaded to <c>filetrees/&lt;tree-hash&gt;</c>.
/// Entries are sorted by <see cref="FileTreeEntry.Name"/> in the serialized form.
/// </summary>
public sealed record FileTreeBlob
{
    /// <summary>All entries in this directory, sorted deterministically by name.</summary>
    public required IReadOnlyList<FileTreeEntry> Entries { get; init; }
}
