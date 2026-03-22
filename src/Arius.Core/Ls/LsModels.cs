using Mediator;

namespace Arius.Core.Ls;

// ── Task 11.1: Mediator LsCommand ─────────────────────────────────────────────

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
}

/// <summary>
/// Mediator command: list files in a snapshot.
/// </summary>
public sealed record LsCommand(LsOptions Options)
    : ICommand<LsResult>;

/// <summary>
/// Result returned by <see cref="LsCommand"/>.
/// </summary>
public sealed record LsResult
{
    public required bool           Success { get; init; }
    public required IReadOnlyList<LsEntry> Entries { get; init; }
    public          string?        ErrorMessage { get; init; }
}

/// <summary>
/// One file entry in the ls output.
/// </summary>
public sealed record LsEntry(
    string         RelativePath,   // forward-slash, relative to archive root
    string         ContentHash,    // 64-char hex
    long?          OriginalSize,   // bytes from chunk index; null if unavailable
    DateTimeOffset Created,
    DateTimeOffset Modified
);
