namespace Arius.Api.Features.Browse;

/// <summary>A repository entry (file or directory) streamed to the file browser.</summary>
public sealed record EntryDto(
    string         RelativePath,
    string         Name,
    string         Kind,          // "file" or "dir"
    int            State,         // raw RepositoryEntryState flags
    StateFlagsDto  StateFlags,    // State decoded into named flags
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
