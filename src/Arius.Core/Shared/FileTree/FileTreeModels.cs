using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// One entry in a persisted filetree node.
/// Files carry a content-hash; directories carry a tree-hash.
/// Timestamps are present for file entries (UTC, ISO-8601). Directory entries omit timestamps.
/// </summary>
public abstract record FileTreeEntry
{
    /// <summary>File or directory name (not a full path).</summary>
    public required string         Name     { get; init; }
}

public sealed record FileEntry : FileTreeEntry
{
    /// <summary>Content-hash for file entries. Lowercase hex SHA-256 (64 chars).</summary>
    public required ContentHash ContentHash { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public required DateTimeOffset Modified { get; init; }
}

public sealed record DirectoryEntry : FileTreeEntry
{
    /// <summary>Tree-hash for directory entries. Lowercase hex SHA-256 (64 chars).</summary>
    public required FileTreeHash FileTreeHash { get; init; }
}

internal sealed record StagedDirectoryEntry : FileTreeEntry
{
    public required string DirectoryId { get; init; }
}
