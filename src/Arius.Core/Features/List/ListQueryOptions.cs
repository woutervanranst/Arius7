namespace Arius.Core.Features.List;

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