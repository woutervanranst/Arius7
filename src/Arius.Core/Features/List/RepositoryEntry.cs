namespace Arius.Core.Features.List;

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
