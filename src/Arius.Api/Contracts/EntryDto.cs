using Arius.Core.Features.ListQuery;

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

/// <summary>A cross-repository search hit: the entry plus its owning repository.</summary>
public sealed record SearchHitDto(long RepoId, string Repo, EntryDto Entry);

/// <summary>Decoded RepositoryEntryState flags.</summary>
public sealed record StateFlagsDto(
    bool LocalPointer,
    bool LocalBinary,
    bool LocalDirectory,
    bool Repository,
    bool RepositoryHydrated,
    bool RepositoryArchived,
    bool RepositoryRehydrating);

internal static class EntryMapping
{
    public static EntryDto ToDto(RepositoryEntry entry)
    {
        var state = entry.State;
        var flags = new StateFlagsDto(
            LocalPointer:          state.HasFlag(RepositoryEntryState.LocalPointer),
            LocalBinary:           state.HasFlag(RepositoryEntryState.LocalBinary),
            LocalDirectory:        state.HasFlag(RepositoryEntryState.LocalDirectory),
            Repository:            state.HasFlag(RepositoryEntryState.Repository),
            RepositoryHydrated:    state.HasFlag(RepositoryEntryState.RepositoryHydrated),
            RepositoryArchived:    state.HasFlag(RepositoryEntryState.RepositoryArchived),
            RepositoryRehydrating: state.HasFlag(RepositoryEntryState.RepositoryRehydrating));

        return entry switch
        {
            RepositoryFileEntry file => new EntryDto(
                file.RelativePath.ToString(),
                file.RelativePath.Name.ToString(),
                "file",
                (int)state,
                flags,
                file.ContentHash?.ToString(),
                file.OriginalSize,
                file.Created,
                file.Modified),

            RepositoryDirectoryEntry dir => new EntryDto(
                dir.RelativePath.ToString(),
                dir.RelativePath.Name.ToString(),
                "dir",
                (int)state,
                flags,
                null, null, null, null),

            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.GetType().Name),
        };
    }
}
