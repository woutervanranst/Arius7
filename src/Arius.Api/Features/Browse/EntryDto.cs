namespace Arius.Api.Contracts;

/// <summary>A repository entry (file or directory) streamed to the file browser.</summary>
public sealed record EntryDto(
    string         RelativePath,
    string         Name,
    string         Kind,          // "file" | "dir"
    int            State,         // raw RepositoryEntryState flags
    StateFlagsDto  StateFlags,    // decoded flags (the State Ring reads this)
    string?        ContentHash,
    long?          OriginalSize,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);

/// <summary>Decoded RepositoryEntryState flags.</summary>
public sealed record StateFlagsDto(
    bool LocalPointer,
    bool LocalBinary,
    bool LocalDirectory,
    bool Repository,
    bool RepositoryHydrated,
    bool RepositoryArchived,
    bool RepositoryRehydrating);
