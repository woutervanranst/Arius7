using Arius.Core.Features.ListQuery;

namespace Arius.Api.Contracts;

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
