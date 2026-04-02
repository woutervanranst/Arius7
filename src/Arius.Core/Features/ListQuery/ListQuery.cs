using Mediator;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Mediator stream query: list repository entries in a snapshot.
/// </summary>
public sealed record ListQuery(ListQueryOptions Options) : IStreamQuery<RepositoryEntry>;

/// <summary>
/// Options for the list query.
/// </summary>
public sealed record ListQueryOptions
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