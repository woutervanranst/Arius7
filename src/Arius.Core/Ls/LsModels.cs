using Mediator;

namespace Arius.Core.Ls;

/// <summary>
/// Options for the ls command.
/// </summary>
public sealed record LsOptions
{
    /// <summary>Snapshot version (partial match). <c>null</c> = latest.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Path prefix filter: only return files whose relative path starts with this value.
    /// <c>null</c> = no prefix filter.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Filename substring filter (case-insensitive).
    /// <c>null</c> = no substring filter.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Whether to recurse into child directories.
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Optional local directory used to merge local filesystem state with cloud state.
    /// </summary>
    public string? LocalPath { get; init; }
}

/// <summary>
/// Mediator stream query: list repository entries in a snapshot.
/// </summary>
public sealed record LsCommand(LsOptions Options)
    : IStreamQuery<RepositoryEntry>;

/// <summary>
/// Base type for entries emitted by the streaming repository listing.
/// </summary>
public abstract record RepositoryEntry(string RelativePath);

/// <summary>
/// A file entry emitted by the repository listing.
/// </summary>
public sealed record RepositoryFileEntry(
    string          RelativePath,
    string?         ContentHash,
    long?           OriginalSize,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool            ExistsInCloud,
    bool            ExistsLocally,
    bool?           HasPointerFile,
    bool?           BinaryExists,
    bool?           Hydrated = null)
    : RepositoryEntry(RelativePath);

/// <summary>
/// A directory entry emitted by the repository listing.
/// Directory paths always end with a trailing slash.
/// </summary>
public sealed record RepositoryDirectoryEntry(
    string RelativePath,
    string? TreeHash,
    bool ExistsInCloud,
    bool ExistsLocally)
    : RepositoryEntry(RelativePath);
