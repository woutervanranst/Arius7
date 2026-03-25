namespace Arius.Core.FileTree;

/// <summary>
/// Type discriminator for entries in a tree blob.
/// </summary>
public enum TreeEntryType
{
    File,
    Dir
}

/// <summary>
/// One entry in a <see cref="TreeBlob"/>.
/// Files carry a content-hash; directories carry a tree-hash.
/// Timestamps are present for file entries (UTC, ISO-8601). Directory entries omit timestamps.
/// </summary>
public sealed record TreeEntry
{
    /// <summary>File or directory name (not a full path).</summary>
    public required string         Name     { get; init; }

    /// <summary>Entry type: <c>file</c> or <c>dir</c>.</summary>
    public required TreeEntryType  Type     { get; init; }

    /// <summary>
    /// Content-hash for file entries; tree-hash for directory entries.
    /// Lowercase hex SHA-256 (64 chars).
    /// </summary>
    public required string         Hash     { get; init; }

    /// <summary>UTC creation timestamp (file entries only; <c>null</c> for dir entries).</summary>
    public          DateTimeOffset? Created  { get; init; }

    /// <summary>UTC last-modified timestamp (file entries only; <c>null</c> for dir entries).</summary>
    public          DateTimeOffset? Modified { get; init; }
}

/// <summary>
/// A Merkle tree node representing a single directory.
/// Serialized as text (one entry per line) and uploaded to <c>filetrees/&lt;tree-hash&gt;</c>.
/// Entries are sorted by <see cref="TreeEntry.Name"/> in the serialized form.
/// </summary>
public sealed record TreeBlob
{
    /// <summary>All entries in this directory, sorted deterministically by name.</summary>
    public required IReadOnlyList<TreeEntry> Entries { get; init; }
}
