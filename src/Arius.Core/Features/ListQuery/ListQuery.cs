using Mediator;

namespace Arius.Core.Features.ListQuery;

// --- QUERY

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
    public RelativePath? Prefix { get; init; }

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


// --- RESPONSE

/// <summary>
/// Base type for entries emitted by the streaming repository listing.
/// </summary>
public abstract record RepositoryEntry(RelativePath RelativePath, RepositoryEntryState State);

/// <summary>
/// A file entry emitted by the repository listing.
/// </summary>
public sealed record RepositoryFileEntry(RelativePath RelativePath, RepositoryEntryState State, ContentHash? ContentHash, long? OriginalSize, DateTimeOffset? Created, DateTimeOffset? Modified) : RepositoryEntry(RelativePath, State);

/// <summary>
/// A directory entry emitted by the repository listing.
/// </summary>
public sealed record RepositoryDirectoryEntry(RelativePath RelativePath, RepositoryEntryState State, FileTreeHash? TreeHash) : RepositoryEntry(RelativePath, State);

/// <summary>
/// Where an entry exists (local disk and/or the repository on blob storage) and, for repository
/// files, the storage-tier state of its chunk. Flags combine: e.g. a file that is archived in the
/// repository and present on disk as pointer + binary is
/// <c>LocalPointer | LocalBinary | Repository | RepositoryArchived</c>.
/// </summary>
[Flags]
public enum RepositoryEntryState
{
    None = 0,

    // ── Local disk ──────────────────────────────────────────────────────────

    /// <summary>A pointer sidecar file exists on disk (files only).</summary>
    LocalPointer = 1 << 0,

    /// <summary>The binary file exists on disk (files only).</summary>
    LocalBinary = 1 << 1,

    /// <summary>The directory exists on disk (directories only).</summary>
    LocalDirectory = 1 << 2,

    // ── Repository (blob storage) ───────────────────────────────────────────

    /// <summary>Present in the snapshot file tree.</summary>
    Repository = 1 << 3,

    /// <summary>Chunk tier hint: hot/cool/cold — downloadable now. Implies <see cref="Repository"/>.</summary>
    RepositoryHydrated = Repository | (1 << 4),

    /// <summary>Chunk tier hint: archive — needs rehydration first. Implies <see cref="Repository"/>.</summary>
    RepositoryArchived = Repository | (1 << 5),

    /// <summary>
    /// Rehydration is pending. Implies <see cref="RepositoryArchived"/>. The chunk index cannot
    /// know this; it is only set by live refinement (see ChunkHydrationStatusQuery).
    /// </summary>
    RepositoryRehydrating = RepositoryArchived | (1 << 6),
}