using Arius.Core.Features.List;
using Mediator;

namespace Arius.Core.Features.ChunkHydrationStatusQuery;

public enum ChunkHydrationStatus
{
    Unknown,
    Available,
    NeedsRehydration,
    RehydrationPending,
}

public sealed record ChunkHydrationStatusQuery(IReadOnlyList<RepositoryFileEntry> Files) : IStreamQuery<ChunkHydrationStatusResult>;

public sealed record ChunkHydrationStatusResult(string RelativePath, string? ContentHash, ChunkHydrationStatus Status);